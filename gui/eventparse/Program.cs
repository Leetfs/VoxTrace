using System;
using System.Collections.Generic;
using System.IO;
using System.Collections;
using System.Text;
using Photon.Client;

class Program
{
    static string Hex(byte[] b, int max)
    {
        if (b == null) return "";
        int n = Math.Min(b.Length, max);
        var sb = new StringBuilder(n * 2);
        for (int i = 0; i < n; i++) sb.Append(b[i].ToString("x2"));
        return sb.ToString();
    }

    static string Fmt(object v, int depth)
    {
        if (v == null) return "null";
        if (v is string str) return "s:" + str.Replace("\\", "\\\\").Replace("\"", "\\\"");
        if (v is byte[] ba) return "b:" + ba.Length + ":" + Hex(ba, Math.Min(ba.Length, 256));
        if (depth > 4) return "o:" + v;
        var dict = v as IDictionary;
        if (dict != null)
        {
            var parts = new List<string>();
            int n = 0;
            foreach (DictionaryEntry de in dict)
            {
                parts.Add(de.Key + ":" + Fmt(de.Value, depth + 1));
                if (++n >= 80) break;
            }
            return "{" + string.Join(",", parts) + "}";
        }
        var en = v as IEnumerable;
        if (en != null)
        {
            var parts = new List<string>();
            int n = 0;
            foreach (var o in en)
            {
                parts.Add(Fmt(o, depth + 1));
                if (++n >= 40) break;
            }
            return "[" + string.Join(",", parts) + "]";
        }
        return "o:" + v;
    }

    static void Main(string[] args)
    {
        string path = args.Length > 0 ? args[0] : "events.bin";
        string outPath = args.Length > 1 ? args[1] : "tools/events.jsonl";
        var proto = new Protocol18();
        var miEvent = typeof(Protocol18).GetMethod("DeserializeEventData");
        var miOp = typeof(Protocol18).GetMethod("DeserializeOperationRequest");
        var flagsEvent = Activator.CreateInstance(miEvent.GetParameters()[2].ParameterType);
        var flagsOp = Activator.CreateInstance(miOp.GetParameters()[1].ParameterType);
        int ok = 0, fail = 0;
        var codes = new Dictionary<int, int>();
        var senders = new Dictionary<int, int>();
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        using var sw = new StreamWriter(outPath, false, new UTF8Encoding(false));
        var buf = new byte[8];
        while (fs.Position < fs.Length)
        {
            if (br.Read(buf, 0, 6) != 6) break;
            char dir = (char)buf[0];
            byte ctype = buf[1];
            int len = BitConverter.ToInt32(buf, 2);
            if (len < 3 || len > 5_000_000) break;
            var msg = br.ReadBytes(len);
            if (msg.Length != len) break;
            byte mtype = msg[1];
            var payload = new byte[len - 2];
            Array.Copy(msg, 2, payload, 0, len - 2);
            try
            {
                var sb = new StreamBuffer(payload);
                if (mtype == 4)
                {
                    var ev = (EventData)miEvent.Invoke(proto, new object[] { sb, null, flagsEvent });
                    codes[ev.Code] = codes.GetValueOrDefault(ev.Code) + 1;
                    senders[ev.Sender] = senders.GetValueOrDefault(ev.Sender) + 1;
                    var parts = new List<string>();
                    foreach (var kv in ev.Parameters)
                    {
                        string val;
                        val = Fmt(kv.Value, 0);
                        parts.Add(kv.Key + "=" + val);
                    }
                    sw.Write("{\"d\":\"" + dir + "\",\"t\":" + ctype + ",\"code\":" + ev.Code +
                             ",\"sender\":" + ev.Sender + ",\"p\":{" + string.Join(",", parts) + "}}\n");
                }
                else if (mtype == 2)
                {
                    var op = (OperationRequest)miOp.Invoke(proto, new object[] { sb, flagsOp });
                    var parts = new List<string>();
                    foreach (var kv in op.Parameters)
                    {
                        string val;
                        if (kv.Value is byte[] ba) val = "b:" + ba.Length + ":" + Hex(ba, 32);
                        else if (kv.Value is string s) val = "s:" + s.Replace("\\", "\\\\").Replace("\"", "\\\"");
                        else val = "o:" + kv.Value;
                        parts.Add(kv.Key + "=" + val);
                    }
                    sw.Write("{\"d\":\"" + dir + "\",\"t\":" + ctype + ",\"op\":" + op.OperationCode +
                             ",\"p\":{" + string.Join(",", parts) + "}}\n");
                }
                ok++;
            }
            catch
            {
                fail++;
            }
        }
        sw.Flush();
        Console.WriteLine($"ok={ok} fail={fail}");
        Console.Write("codes: ");
        foreach (var kv in codes) Console.Write($"{kv.Key}:{kv.Value} ");
        Console.WriteLine();
        Console.Write("senders: ");
        foreach (var kv in senders) Console.Write($"{kv.Key}:{kv.Value} ");
        Console.WriteLine();
    }
}
