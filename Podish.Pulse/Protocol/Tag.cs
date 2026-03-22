namespace Podish.Pulse.Protocol;

/// <summary>
/// A tag preceding a value in a tagstruct.
/// </summary>
public enum Tag : byte
{
    String = (byte)'t',
    StringNull = (byte)'N',
    U32 = (byte)'L',
    U8 = (byte)'B',
    U64 = (byte)'R',
    S64 = (byte)'r',
    SampleSpec = (byte)'a',
    Arbitrary = (byte)'x',
    BooleanTrue = (byte)'1',
    BooleanFalse = (byte)'0',
    TimeVal = (byte)'T',
    Usec = (byte)'U',
    ChannelMap = (byte)'m',
    CVolume = (byte)'v',
    PropList = (byte)'P',
    Volume = (byte)'V',
    FormatInfo = (byte)'f',
}
