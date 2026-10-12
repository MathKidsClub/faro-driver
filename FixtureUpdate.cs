using FaroArm.Interfaces;

namespace FaroDriverV2
{
    // Deterministic test data only. No production fallback uses this type.
    internal sealed class FixtureUpdate : IFaroArmUpdate
    {
        public double X { get; set; } = 10.25;
        public double Y { get; set; } = -20.5;
        public double Z { get; set; } = 30.75;
        public double A { get; set; } = 1;
        public double B { get; set; } = 2;
        public double C { get; set; } = 3;
        public double Angle1 { get; set; }
        public double Angle2 { get; set; }
        public double Angle3 { get; set; }
        public double Angle4 { get; set; }
        public double Angle5 { get; set; }
        public double Angle6 { get; set; }
        public double Angle7 { get; set; }
        public int Buttons { get; set; }
        public int EndStops { get; set; }
        public bool EncodersReferenced { get; set; }
        public bool ArmAtRest { get; set; }
        public int TimeStamp { get; set; }
        public double Temperature { get; set; }
        public double RawEnc1Temperature { get; set; }
        public double RawEnc2Temperature { get; set; }
        public double RawEnc3Temperature { get; set; }
        public double RawEnc4Temperature { get; set; }
        public double RawEnc5Temperature { get; set; }
        public double RawEnc6Temperature { get; set; }
        public double RawEnc7Temperature { get; set; }
        public double RawEnc8Temperature { get; set; }
        public double RawEnc9Temperature { get; set; }
    }
}
