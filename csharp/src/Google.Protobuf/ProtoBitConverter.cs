namespace Google.Protobuf
{
    public static class ProtoBitConverter
    {
        public static unsafe uint FloatToUInt32Bits(float value)
        {
            uint val = *((uint*)&value);
            return val;
        }
    }
}