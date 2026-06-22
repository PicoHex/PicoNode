namespace PicoNode.Web.Session.Abs;

public static class SessionExtensions
{
    public static string? GetString(this ISession session, string key)
    {
        return session.TryGetValue(key, out var value) && value is not null
            ? Encoding.UTF8.GetString(value)
            : null;
    }

    public static void SetString(this ISession session, string key, string? value)
    {
        if (value is null)
        {
            session.Remove(key);
            return;
        }
        session.SetValue(key, Encoding.UTF8.GetBytes(value));
    }

    public static int GetInt32(this ISession session, string key)
    {
        return session.TryGetValue(key, out var value) && value is not null && value.Length >= 4
            ? BitConverter.ToInt32(value, 0)
            : 0;
    }

    public static void SetInt32(this ISession session, string key, int value)
    {
        session.SetValue(key, BitConverter.GetBytes(value));
    }

    public static long GetInt64(this ISession session, string key)
    {
        return session.TryGetValue(key, out var value) && value is not null && value.Length >= 8
            ? BitConverter.ToInt64(value, 0)
            : 0L;
    }

    public static void SetInt64(this ISession session, string key, long value)
    {
        session.SetValue(key, BitConverter.GetBytes(value));
    }

    public static bool GetBoolean(this ISession session, string key)
    {
        return session.TryGetValue(key, out var value)
            && value is not null
            && value.Length >= 1
            && value[0] != 0;
    }

    public static void SetBoolean(this ISession session, string key, bool value)
    {
        session.SetValue(key, [(byte)(value ? 1 : 0)]);
    }
}
