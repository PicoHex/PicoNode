namespace PicoNode.Http;

public enum Http2SettingId : ushort
{
    HeaderTableSize = 0x1,
    EnablePush = 0x2,
    MaxConcurrentStreams = 0x3,
    InitialWindowSize = 0x4,
    MaxFrameSize = 0x5,
    MaxHeaderListSize = 0x6,
}

public readonly record struct Http2Setting(Http2SettingId Id, uint Value);
