# FARO arm stream driver

A small Windows console application that reads a FARO arm through the installed
vendor libraries and publishes its updates to a local ZeroMQ subscriber.
`test.py` is a standalone, read-only Python stream viewer and transport tester.

This is an unofficial application wrapper, **not a replacement USB driver**.
It still requires the installed FARO Windows driver and its registered hardware
backend (`FAROAtlServerApp5.exe`). FARO Manager is used for probe compensation,
but its UI must be closed while this application connects to the arm.

The repository contains only driver source, build support, this README and
`test.py`. It contains no FARO DLLs, decompiled vendor code, calibration files,
measurement exports, engine data or metrology application. Obtain dependencies
from your own appropriate FARO installation; they are not redistributed here.

## Requirements

- Windows x64. The tested configuration uses FARO Manager/USB driver 6.11.21.
  Other driver releases and arm models require separate verification.
- Installed .NET Framework 4.8 runtime and its developer/targeting pack.
- A .NET SDK supporting SDK-style builds; the publication build was checked with
  .NET SDK 8.0.416. The resulting executable targets **net48**, not .NET 8.
- The matching Visual C++ runtime needed by the vendor's x64 mixed-mode/native DLLs.
- Python 3.9 or later and `pyzmq` for the test app.

For the targeting pack, see Microsoft's
[.NET Framework 4.8 developer pack](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48).
Use the hardware driver and runtime installers appropriate to your FARO device.
Copying DLLs alone does not install or register the hardware backend.

## Compile

From PowerShell in the repository directory:

```powershell
python -m pip install pyzmq

# Point at the folder containing the matching locally installed dependencies.
.\Build.ps1 -VendorDir 'C:\PATH\TO\FARO\DEPENDENCIES'
```

The dependency directory must contain:

```text
FaroArm.Interfaces.dll
FaroArm.Net.dll
FaroArm.Native.dll
NetMQ.dll
AsyncIO.dll
Newtonsoft.Json.dll
NaCl.dll
System.Memory.dll
System.Buffers.dll
System.Numerics.Vectors.dll
System.Runtime.CompilerServices.Unsafe.dll
System.Threading.Tasks.Extensions.dll
```

Use a consistent set from the same working installation; do not mix arbitrary
versions. Alternatively, place these files in the ignored `vendor` folder and
run `.\Build.ps1` without parameters. The build checks for missing dependencies
and copies the required runtime DLLs next to `app\FaroDriver.exe`.

If PowerShell script execution is blocked, use the equivalent direct command:

```powershell
dotnet build .\FaroDriver.csproj -c Release -o .\app --nologo '-p:VendorDir=C:\PATH\TO\FARO\DEPENDENCIES'
```

Do not upload `app`, `vendor`, `bin` or `obj`; they are excluded by `.gitignore`.
The build does not require reverse-engineering tools or another application repo.

## Check the build without an arm

```powershell
.\app\FaroDriver.exe --diagnose
.\app\FaroDriver.exe --self-test
python .\test.py --fixture-test
```

`--diagnose` checks dependency loading without calling the hardware connection
API. `--self-test` checks snapshot immutability, status preservation, non-finite
coordinate handling and synthetic-mode isolation. The Python fixture test starts
a temporary synthetic driver on a separate loopback port, validates multipart
packets and clean exit, and never connects to the arm. Synthetic packets use
`driver_v2_test_fixture`, `Simulation: true` and `Connected: false`; they are not
measurement evidence. Fixture mode is refused on production port 5457.

These checks do not establish hardware connectivity or accuracy.

## Run with an arm

Close FARO Manager and any other arm-data publisher. Connect/power the arm and
reference its encoders as required by the device. In the first PowerShell window:

```powershell
# Discover arms; this command exits after listing them.
.\app\FaroDriver.exe --list --discover-seconds 20

# Continuous real stream; substitute the serial shown by --list.
.\app\FaroDriver.exe --serial YOUR_ARM_SERIAL
```

In a second window, from the repository directory:

```powershell
python .\test.py
python .\test.py --count 100
python .\test.py --raw --count 10
python .\test.py --status
```

The viewer displays vendor XYZ, raw button/end-stop state, probe/reference status,
packet rate and sequence gaps. `--raw` shows all fields, including ABC and joint
angles. It does not capture dimensions, change probes, recalibrate or shut down
the real driver. Ctrl+C stops the viewer only; Ctrl+C in the driver window stops
the driver. `--status` makes only a read-only control request.

Other viewer options: `--timeout 10` changes the idle timeout; `--duration 30`
sets a bounded viewing period; `--connect` and `--control` override endpoints.
Tester exit codes: 0 completed/interrupted normally, 1 timeout/test failure,
2 dependency/argument error. It exits after five seconds without valid JSON
packets by default. Receiving disconnected or synthetic packets is not proof
of usable arm data.

Driver options are shown by `.\app\FaroDriver.exe --help`. `--seconds 30` provides
a bounded hardware run. Driver exit codes:

| Code | Meaning |
| --- | --- |
| 0 | Successful operation; meaning depends on the selected mode |
| 2 | No arm discovered |
| 3 | Dependency, connection, initialization or publisher error |
| 4 | No callbacks received or stream stopped |
| 6 | Vendor API call exceeded its deadline |
| 64 | Invalid arguments |

`Connect: Success (0)` only reports the vendor connection call's result. It does
not prove callbacks are arriving. Check increasing callback counts and actual
packets in `test.py`. A hardware stream fails if first callbacks do not arrive
within the configured timeout or if an established stream stops for three seconds.

## Stream format

- Publisher: `tcp://127.0.0.1:5457` by default.
- Each message has exactly two frames: UTF-8 topic `FaroArmUpdate`, then JSON.
- Control: `tcp://127.0.0.1:5458`, a separate ZeroMQ REQ/REP endpoint.
- Both endpoints are loopback-only. They are not authenticated: another local
  process can access them. Do not forward the control endpoint to a network.
- Supported control JSON commands are `status`, `list_probes`, `select_probe`
  with an integer `probe_id`, and `shutdown`. The last two change state;
  `test.py` deliberately exposes neither. Calibrate in FARO Manager, not here.

| Fields | Interpretation |
| --- | --- |
| `schema_version`, `producer` | Real stream uses `legacy_flat_v0`, `FaroDriver_v2` |
| `X`, `Y`, `Z` | Unchanged vendor coordinates; mm in the tested configuration |
| `A`, `B`, `C` | Unchanged vendor orientation angles; degrees in the tested configuration |
| `J1` through `J7` | Vendor joint angles; degrees in the tested configuration; unused joints can be zero |
| `Buttons` | Raw button bitmask; mapping hints are supplied separately |
| `EndStops` | Raw status word, not a Boolean |
| `EncodersReferenced`, `ArmAtRest`, `Connected` | Device/source state; not an accuracy certificate |
| `Temperature` | Vendor temperature; degrees Celsius in the tested configuration, not workpiece temperature |
| `TimeStamp` | Raw vendor timestamp; do not assume it is a precise UTC acquisition time |
| `receive_timestamp_utc` | Driver callback-receipt UTC timestamp, not guaranteed physical sampling time |
| `Sequence`, `source_instance_id` | Per-process sequence and new UUID on each driver restart |
| `serial_number` | Selected arm's serial, populated at runtime |
| `probe_contract`, `probe`, `probe_revision` | Saved-probe metadata, compensation state and selection revision |
| `position_state` | `UNKNOWN` unless the explicit verified ball-centre contract below is satisfied |

There is no coordinate conversion, axis swapping or probe projection in this
driver. Verify vendor units and the orientation convention for your own device;
do not assume an Euler rotation order from the names ABC alone. Joint angles can
wrap: a signed angle transition is not by itself evidence of an encoder fault.

PUB/SUB is lossy, not an archival guarantee. The callback snapshots its input;
the publisher owns its socket on a separate thread and uses a bounded queue.
Sequence gaps can indicate queue or transport/subscriber losses. On a new
`source_instance_id`, reset sequence and button-edge tracking. Do not mix data
across a probe revision or driver restart without checking the reference contract.

## Ball centre, probe compensation and surface dimensions

Physical testing on one legacy six-axis USB setup established compensated
ball-centre coordinates after saved hole compensation. That finding must not
be assumed for every FARO model, probe or software combination.

The public build therefore defaults to `position_state: UNKNOWN` while still
streaming the raw vendor coordinates. After independently verifying the coordinate
reference for your selected setup, an explicit opt-in is available:

```powershell
.\app\FaroDriver.exe --serial YOUR_ARM_SERIAL --verified-ball-centre YOUR_ARM_SERIAL
```

This asserts operator validation for that serial; it does not perform validation.
The driver additionally requires a uniquely resolved saved probe, a saved
compensation with a non-default reference, valid vendor compensation and an active
probe before labelling packets `BALL_CENTRE`. Other serials remain `UNKNOWN`.
Revalidate when the arm, driver or physical probe setup changes. Never enable the
flag just to make a measurement application's warning disappear.

Probe metadata contains saved ID/name, diameter/radius, active compensation date,
offset vector, maximum error, two-sigma value and validity. Some vendor backends
return a transient active-probe ID of -1 and zero point count even for a saved
profile. Resolution uses a unique match of actual compensation/date/diameter/name,
not name alone. These values should not be mistaken for automatic calibration.

When output really is ball centre, **do not project from the last joint to the
tip again**. However, ball-centre XYZ is not the contact-surface position:
feature fitting still needs appropriate ball-radius compensation. For ideal
circular sections, the ball-centre radius is smaller than an internal bore's
radius and larger than an external journal's radius. Use the actual compensated
probe diameter, the correct surface model and internal/external sign, once.

## Buttons, limits and measurement precautions

On the tested six-axis setup, raw mask `2` was green/capture and mask `1` was
red/advance. The driver publishes `button_mapping.capture_bit: 2` and
`advance_bit: 1` as these mapping hints. Verify colours/functions on other hardware.
They are masks, not button counts. A measurement client should handle press edges,
ignore a held startup button and avoid assigning an action when both are pressed.

On that setup, joint-limit bits were bits 1 through 7: `EndStops & 0xFE`.
Bit 0 and changing upper bits could be nonzero in valid positions; testing the
whole word for nonzero falsely reported limits. `test.py` shows the raw word and
this candidate joint mask; confirm the interpretation for your device. Never
ignore a real limit alarm or invert flag polarity to obtain usable-looking data.

The viewer is a diagnostic display, **not a measurement qualification gate**.
A real client must enforce source freshness, finite coordinates, correct verified
reference, valid active compensation, referenced encoders, device limit/rest
conditions and consistent probe/source identity. A displayed value is not proof
of contact or accuracy.

Light contact, comfortable configurations, stable mounting and independent
lift/recontact measurements matter. Steady pressure can produce a steady displaced
reading; averaging or a quiet three-frame burst cannot prove accuracy. FARO warns
about near-stop/full-extension loading, vibration and temperature changes.
[FARO accuracy guidance](https://knowledge.faro.com/Hardware/FaroArm_and_ScanArm/FaroArm_and_ScanArm/Understanding_Accuracy_with_the_FaroArm_and_ScanArm).

Probe compensation passing in Manager does not certify the arm's entire working
volume. Check reference artifacts and independent repeats before trusting
dimensions; this driver does not identify encoder faults or establish calibration
traceability.

## Troubleshooting

- **Success (0), no packets:** check the driver's callback counts, discovery,
  correct serial, arm power/cable/reference state and whether Manager or another
  source is using the device. Dependency-only `--diagnose` and enumeration-only
  `--list` do not publish a live stream.
- **Missing dependencies / mixed-mode load error:** use matching x64 vendor DLLs,
  .NET Framework 4.8 and the matching Visual C++ runtime; leave the generated
  `FaroDriver.exe.config` next to the executable.
- **Port already in use:** stop the competing publisher or choose distinct
  `--bind`/`--control` ports and matching Python endpoints. The driver binds
  before changing arm state, so a conflict fails early.
- **Unknown reference or invalid compensation:** inspect `python test.py --status`;
  select/compensate the actual mounted probe in Manager with this driver stopped.
  Do not invent a tip offset or treat nominal diameter as a valid compensation.
- **Stream stopped:** the viewer's cached display is not fresh evidence. Check
  hardware and restart intentionally; reset tracking for the new driver UUID.

Native calls have deadlines. A deadline exit terminates only this client and may
prevent normal cleanup. Normal shutdown disables its updates, restores its change
to encoder-reference enablement and disconnects; it does not forcibly terminate
the vendor server or unrelated processes.
