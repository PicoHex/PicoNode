namespace PicoNode.Http.Internal.Hpack;

public static class StaticTable
{
    public const int EntryCount = 61;

    public static readonly (string Name, string Value)[] Entries = new (string, string)[62]; // 0-based, index 0 unused

    static StaticTable()
    {
        Entries[1] = (":authority", "");
        Entries[2] = (":method", "GET");
        Entries[3] = (":method", "POST");
        Entries[4] = (":path", "/");
        Entries[5] = (":path", "/index.html");
        Entries[6] = (":scheme", "http");
        Entries[7] = (":scheme", "https");
        Entries[8] = (":status", "200");
        Entries[9] = (":status", "204");
        Entries[10] = (":status", "206");
        Entries[11] = (":status", "304");
        Entries[12] = (":status", "400");
        Entries[13] = (":status", "404");
        Entries[14] = (":status", "500");
        Entries[15] = ("accept-charset", "");
        Entries[16] = ("accept-encoding", "gzip, deflate");
        Entries[17] = ("accept-language", "");
        Entries[18] = ("accept-ranges", "");
        Entries[19] = ("accept", "");
        Entries[20] = ("access-control-allow-origin", "");
        Entries[21] = ("age", "");
        Entries[22] = ("allow", "");
        Entries[23] = ("authorization", "");
        Entries[24] = ("cache-control", "");
        Entries[25] = ("content-disposition", "");
        Entries[26] = ("content-encoding", "");
        Entries[27] = ("content-language", "");
        Entries[28] = ("content-length", "");
        Entries[29] = ("content-location", "");
        Entries[30] = ("content-range", "");
        Entries[31] = ("content-type", "");
        Entries[32] = ("cookie", "");
        Entries[33] = ("date", "");
        Entries[34] = ("etag", "");
        Entries[35] = ("expect", "");
        Entries[36] = ("expires", "");
        Entries[37] = ("from", "");
        Entries[38] = ("host", "");
        Entries[39] = ("if-match", "");
        Entries[40] = ("if-modified-since", "");
        Entries[41] = ("if-none-match", "");
        Entries[42] = ("if-range", "");
        Entries[43] = ("if-unmodified-since", "");
        Entries[44] = ("last-modified", "");
        Entries[45] = ("link", "");
        Entries[46] = ("location", "");
        Entries[47] = ("max-forwards", "");
        Entries[48] = ("proxy-authenticate", "");
        Entries[49] = ("proxy-authorization", "");
        Entries[50] = ("range", "");
        Entries[51] = ("referer", "");
        Entries[52] = ("refresh", "");
        Entries[53] = ("retry-after", "");
        Entries[54] = ("server", "");
        Entries[55] = ("set-cookie", "");
        Entries[56] = ("strict-transport-security", "");
        Entries[57] = ("transfer-encoding", "");
        Entries[58] = ("user-agent", "");
        Entries[59] = ("vary", "");
        Entries[60] = ("via", "");
        Entries[61] = ("www-authenticate", "");
    }
}
