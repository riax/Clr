namespace Hyperletter.Abstraction {
    public enum LetterType : byte {
        Ack         = 0x01,
        Initialize  = 0x02,
        Heartbeat   = 0x03,
        Batch       = 0x04,
        User        = 0x64,
    }
}