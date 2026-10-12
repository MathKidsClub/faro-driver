"""Read-only FARO stream tester; optional synthetic transport test, no metrology app."""

import argparse
import json
import math
from pathlib import Path
import socket as tcp_socket
import subprocess
import sys
import time

try:
    import zmq
except ImportError:
    print("Install the dependency with: python -m pip install pyzmq", file=sys.stderr)
    raise SystemExit(2)


TOPIC = b"FaroArmUpdate"


def positive_seconds(value):
    number = float(value)
    if not math.isfinite(number) or number <= 0:
        raise argparse.ArgumentTypeError("must be a positive finite number")
    return number


def nonnegative_count(value):
    number = int(value)
    if number < 0:
        raise argparse.ArgumentTypeError("must be zero or greater")
    return number


def parse_packet(data):
    def invalid_constant(value):
        raise ValueError("non-finite JSON constant: " + value)
    packet = json.loads(data, parse_constant=invalid_constant)
    if not isinstance(packet, dict):
        raise ValueError("expected a JSON object")
    return packet


def finite(value):
    if not isinstance(value, (int, float)) or isinstance(value, bool):
        return False
    try:
        return math.isfinite(value)
    except OverflowError:
        return False


def number(value):
    return f"{value:10.4f}" if finite(value) else f"{str(value):>10}"


def source_flags(packet):
    if packet.get("Simulation") is True or packet.get("schema_version") == "driver_v2_test_fixture":
        return "SYNTHETIC TEST: NOT MEASUREMENT DATA"
    reasons = []
    if packet.get("Connected") is not True:
        reasons.append("disconnected/invalid")
    if not all(finite(packet.get(axis)) for axis in ("X", "Y", "Z")):
        reasons.append("no finite XYZ")
    if packet.get("EncodersReferenced") is not True:
        reasons.append("encoders not referenced")
    if packet.get("ArmAtRest") is True:
        reasons.append("arm parked")
    stops = packet.get("EndStops")
    if isinstance(stops, int) and not isinstance(stops, bool) and stops & 0xFE:
        reasons.append(f"joint flags 0x{stops & 0xFE:02X} (verify model mask)")
    if packet.get("position_state") != "BALL_CENTRE":
        reasons.append("coordinate reference UNKNOWN")
    probe = packet.get("probe")
    if not isinstance(probe, dict) or probe.get("active") is not True or probe.get("compensation_valid") is not True:
        reasons.append("no active valid probe compensation")
    return "; ".join(reasons) if reasons else "source flags clear; accuracy NOT certified"


def read_status(context, endpoint, timeout):
    with context.socket(zmq.REQ) as request:
        request.setsockopt(zmq.LINGER, 0)
        request.connect(endpoint)
        request.send_json({"command": "status"})
        if not request.poll(int(timeout * 1000)):
            print("No status reply: is the driver running on this control endpoint?", file=sys.stderr)
            return 1
        try:
            reply = parse_packet(request.recv())
        except (ValueError, UnicodeError) as exc:
            print(f"Invalid status reply: {exc}", file=sys.stderr)
            return 1
        print(json.dumps(reply, indent=2, sort_keys=True))
        return 0 if reply.get("ok") is True else 1


def stream(context, args):
    process = None
    received = gaps = restarts = 0
    previous_sequence = previous_source = previous_revision = None
    latest_flags = None
    started = last_received = time.monotonic()
    last_display = 0.0
    endpoint = args.connect
    with context.socket(zmq.SUB) as subscriber:
        subscriber.setsockopt(zmq.SUBSCRIBE, TOPIC)
        subscriber.setsockopt(zmq.LINGER, 0)
        if args.fixture_test:
            if sys.platform != "win32":
                print("The compiled fixture driver requires Windows.", file=sys.stderr)
                return 2
            driver = Path(args.driver).resolve()
            if not driver.is_file():
                print(f"Build the driver first; executable not found: {driver}", file=sys.stderr)
                return 2
            with tcp_socket.socket() as reservation:
                reservation.bind(("127.0.0.1", 0))
                port = reservation.getsockname()[1]
            endpoint = f"tcp://127.0.0.1:{port}"
        subscriber.connect(endpoint)
        try:
            if args.fixture_test:
                process = subprocess.Popen(
                    [str(driver), "--fixture", "--bind", endpoint, "--seconds", "4"],
                    creationflags=subprocess.CREATE_NO_WINDOW,
                )
                print("SYNTHETIC TRANSPORT TEST: no arm connection; not calibration.")
            print(f"Listening on {endpoint}; Ctrl+C stops this tester only.")
            while True:
                now = time.monotonic()
                if now - last_received >= args.timeout:
                    print(f"\nNo valid packets for {args.timeout:g} s. Check driver output, arm connection and endpoint.", file=sys.stderr)
                    return 1
                if args.duration and now - started >= args.duration:
                    return 0 if received else 1
                if args.fixture_test and now - started >= 12:
                    print("Fixture test did not receive 20 valid packets.", file=sys.stderr)
                    return 1
                if not subscriber.poll(100):
                    if time.monotonic() - last_received >= args.timeout:
                        print(f"\nNo valid packets for {args.timeout:g} s. Check driver output, arm connection and endpoint.", file=sys.stderr)
                        return 1
                    continue
                frames = subscriber.recv_multipart()
                if len(frames) != 2 or frames[0] != TOPIC:
                    print("Ignored unexpected topic/framing.", file=sys.stderr)
                    continue
                try:
                    packet = parse_packet(frames[1])
                except (ValueError, UnicodeError) as exc:
                    print(f"Ignored invalid JSON: {exc}", file=sys.stderr)
                    continue
                if args.fixture_test:
                    if not (packet.get("schema_version") == "driver_v2_test_fixture"
                            and packet.get("Simulation") is True and packet.get("Connected") is False
                            and tuple(packet.get(a) for a in "XYZ") == (10.25, -20.5, 30.75)
                            and tuple(packet.get(a) for a in "ABC") == (1, 2, 3)):
                        print("Fixture packet validation failed.", file=sys.stderr)
                        return 1
                now = last_received = time.monotonic()
                sequence, source = packet.get("Sequence"), packet.get("source_instance_id")
                revision = packet.get("probe_revision")
                if received and source != previous_source:
                    restarts += 1
                    previous_sequence = None
                    print("\nDriver instance changed; sequence tracking reset.")
                if received and revision != previous_revision:
                    print("\nProbe revision changed; do not mix measurement sets.")
                if isinstance(sequence, int) and not isinstance(sequence, bool) and isinstance(previous_sequence, int):
                    if sequence <= previous_sequence:
                        if args.fixture_test:
                            print("Non-increasing fixture sequence.", file=sys.stderr)
                            return 1
                        print("\nSequence reset/out of order within driver instance.", file=sys.stderr)
                    else:
                        gaps += max(0, sequence - previous_sequence - 1)
                previous_sequence, previous_source, previous_revision = sequence, source, revision
                received += 1
                if args.raw:
                    print(json.dumps(packet, sort_keys=True), flush=True)
                else:
                    flags = source_flags(packet)
                    if flags != latest_flags:
                        print("\nStatus: " + flags, flush=True)
                        latest_flags = flags
                    if now - last_display >= .1:
                        probe = packet.get("probe")
                        if not isinstance(probe, dict):
                            probe = {}
                        stops = packet.get("EndStops")
                        stop_text = f"0x{stops:X}" if isinstance(stops, int) else str(stops)
                        print(
                            f"\rseq {sequence} {received / max(now - started, .001):.1f} Hz gaps {gaps} | "
                            f"XYZ {' '.join(number(packet.get(a)) for a in 'XYZ')} | "
                            f"buttons {packet.get('Buttons')} stops {stop_text} | "
                            f"{packet.get('position_state', 'UNKNOWN')} probe {probe.get('name', '-')}   ",
                            end="", flush=True,
                        )
                        last_display = now
                if args.fixture_test and received >= 20:
                    code = process.wait(timeout=10)
                    if code != 0:
                        print(f"\nFixture driver exited with code {code}.", file=sys.stderr)
                        return 1
                    print("\nPASS: multipart topic, JSON, immutable fixture values, increasing sequence and clean exit.")
                    return 0
                if args.count and received >= args.count:
                    return 0
        except KeyboardInterrupt:
            return 0
        finally:
            if process is not None and process.poll() is None:
                # Only terminate the synthetic process created by this invocation.
                process.terminate()
                process.wait(timeout=10)
            print(f"\nReceived {received} packets; sequence gaps {gaps}; driver restarts {restarts}.", file=sys.stderr)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--connect", default="tcp://127.0.0.1:5457")
    parser.add_argument("--control", default="tcp://127.0.0.1:5458")
    parser.add_argument("--raw", action="store_true", help="print complete packets as JSON")
    parser.add_argument("--count", type=nonnegative_count, default=0, help="stop after N packets; 0 means continuous")
    parser.add_argument("--timeout", type=positive_seconds, default=5.0, help="exit after N seconds without a valid packet")
    parser.add_argument("--duration", type=positive_seconds, help="stop after N seconds")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--status", action="store_true", help="read-only control status request")
    mode.add_argument("--fixture-test", action="store_true", help="test synthetic driver transport without an arm")
    parser.add_argument("--driver", default=str(Path(__file__).resolve().parent / "app" / "FaroDriver.exe"))
    args = parser.parse_args()
    if args.fixture_test and (args.raw or args.count or args.duration):
        parser.error("--fixture-test runs its own fixed validation; omit --raw, --count and --duration")
    with zmq.Context() as context:
        return read_status(context, args.control, args.timeout) if args.status else stream(context, args)


if __name__ == "__main__":
    raise SystemExit(main())
