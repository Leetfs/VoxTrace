using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
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

    static void JsonString(TextWriter w, string s)
    {
        w.Write('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': w.Write("\\\""); break;
                case '\\': w.Write("\\\\"); break;
                case '\n': w.Write("\\n"); break;
                case '\r': w.Write("\\r"); break;
                case '\t': w.Write("\\t"); break;
                default:
                    if (c < 0x20) w.Write("\\u" + ((int)c).ToString("x4"));
                    else w.Write(c);
                    break;
            }
        }
        w.Write('"');
    }

    static void JsonValue(TextWriter w, object v, int depth)
    {
        if (v == null) { w.Write("null"); return; }
        if (v is string s) { JsonString(w, s); return; }
        if (v is bool b) { w.Write(b ? "true" : "false"); return; }
        if (v is byte by) { w.Write(by.ToString(CultureInfo.InvariantCulture)); return; }
        if (v is sbyte sbv) { w.Write(sbv.ToString(CultureInfo.InvariantCulture)); return; }
        if (v is short sh) { w.Write(sh.ToString(CultureInfo.InvariantCulture)); return; }
        if (v is ushort ush) { w.Write(ush.ToString(CultureInfo.InvariantCulture)); return; }
        if (v is int i) { w.Write(i.ToString(CultureInfo.InvariantCulture)); return; }
        if (v is uint ui) { w.Write(ui.ToString(CultureInfo.InvariantCulture)); return; }
        if (v is long l) { w.Write(l.ToString(CultureInfo.InvariantCulture)); return; }
        if (v is ulong ul) { w.Write(ul.ToString(CultureInfo.InvariantCulture)); return; }
        if (v is float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) JsonString(w, f.ToString(CultureInfo.InvariantCulture));
            else w.Write(f.ToString("R", CultureInfo.InvariantCulture));
            return;
        }
        if (v is double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) JsonString(w, d.ToString(CultureInfo.InvariantCulture));
            else w.Write(d.ToString("R", CultureInfo.InvariantCulture));
            return;
        }
        if (v is byte[] ba)
        {
            w.Write("{\"$b\":" + ba.Length + ",\"hex\":");
            JsonString(w, Hex(ba, 4096));
            w.Write("}");
            return;
        }
        if (depth > 6)
        {
            w.Write("{\"$type\":");
            JsonString(w, v.GetType().Name);
            w.Write(",\"value\":");
            JsonString(w, v.ToString());
            w.Write("}");
            return;
        }
        var dictType = v.GetType();
        bool genericDict = false;
        foreach (var it in dictType.GetInterfaces())
        {
            if (it.IsGenericType && it.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            {
                genericDict = true;
                break;
            }
            if (it.IsGenericType && it.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                var arg = it.GetGenericArguments()[0];
                if (arg.IsGenericType && arg.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                {
                    genericDict = true;
                    break;
                }
            }
        }
        if (v is IDictionary dict)
        {
            w.Write('{');
            bool first = true;
            foreach (DictionaryEntry de in dict)
            {
                if (!first) w.Write(',');
                first = false;
                JsonString(w, Convert.ToString(de.Key, CultureInfo.InvariantCulture));
                w.Write(':');
                JsonValue(w, de.Value, depth + 1);
            }
            w.Write('}');
            return;
        }
        if (genericDict)
        {
            w.Write('{');
            bool first = true;
            foreach (var item in (IEnumerable)v)
            {
                if (item == null) continue;
                var t = item.GetType();
                var kp = t.GetProperty("Key");
                var vp = t.GetProperty("Value");
                if (kp == null || vp == null) continue;
                if (!first) w.Write(',');
                first = false;
                JsonString(w, Convert.ToString(kp.GetValue(item), CultureInfo.InvariantCulture));
                w.Write(':');
                JsonValue(w, vp.GetValue(item), depth + 1);
            }
            w.Write('}');
            return;
        }
        if (v is IEnumerable en)
        {
            w.Write('[');
            bool first = true;
            foreach (var o in en)
            {
                if (!first) w.Write(',');
                first = false;
                JsonValue(w, o, depth + 1);
            }
            w.Write(']');
            return;
        }
        w.Write("{\"$type\":");
        JsonString(w, v.GetType().Name);
        w.Write(",\"value\":");
        JsonString(w, Convert.ToString(v, CultureInfo.InvariantCulture));
        w.Write("}");
    }

    static void Main(string[] args)
    {
        string path = args.Length > 0 ? args[0] : "events.bin";
        string outPath = args.Length > 1 ? args[1] : "photon.jsonl";
        var proto = new Protocol18();

        var miEvent = typeof(Protocol18).GetMethod("DeserializeEventData");
        var miOp = typeof(Protocol18).GetMethod("DeserializeOperationRequest");
        var miResp = typeof(Protocol18).GetMethod("DeserializeOperationResponse");

        object flagsEvent = Activator.CreateInstance(miEvent.GetParameters()[miEvent.GetParameters().Length - 1].ParameterType);
        object flagsOp = Activator.CreateInstance(miOp.GetParameters()[miOp.GetParameters().Length - 1].ParameterType);
        object flagsResp = miResp != null
            ? Activator.CreateInstance(miResp.GetParameters()[miResp.GetParameters().Length - 1].ParameterType)
            : null;

        int ok = 0, fail = 0, unknown = 0;
        var codes = new Dictionary<int, int>();
        var ops = new Dictionary<int, int>();
        var senders = new Dictionary<int, int>();

        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        using var sw = new StreamWriter(outPath, false, new UTF8Encoding(false));
        var buf = new byte[8];

        while (fs.Position < fs.Length)
        {
            if (br.Read(buf, 0, 6) != 6) break;
            char dir = (char)buf[0];
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
                sw.Write("{\"dir\":");
                JsonString(sw, dir.ToString());
                sw.Write(",\"mtype\":" + mtype);

                if (mtype == 4)
                {
                    var ev = (EventData)miEvent.Invoke(proto, new object[] { sb, null, flagsEvent });
                    codes[ev.Code] = codes.GetValueOrDefault(ev.Code) + 1;
                    senders[ev.Sender] = senders.GetValueOrDefault(ev.Sender) + 1;
                    sw.Write(",\"code\":" + ev.Code + ",\"sender\":" + ev.Sender + ",\"params\":");
                    JsonValue(sw, ev.Parameters, 0);
                }
                else if (mtype == 2)
                {
                    var op = (OperationRequest)miOp.Invoke(proto, new object[] { sb, flagsOp });
                    ops[op.OperationCode] = ops.GetValueOrDefault(op.OperationCode) + 1;
                    sw.Write(",\"op\":" + op.OperationCode + ",\"params\":");
                    JsonValue(sw, op.Parameters, 0);
                }
                else if (mtype == 3 && miResp != null)
                {
                    var resp = (OperationResponse)miResp.Invoke(proto, new object[] { sb, flagsResp });
                    ops[resp.OperationCode] = ops.GetValueOrDefault(resp.OperationCode) + 1;
                    sw.Write(",\"op\":" + resp.OperationCode + ",\"ret\":" + resp.ReturnCode);
                    sw.Write(",\"debug\":");
                    JsonString(sw, resp.DebugMessage ?? "");
                    sw.Write(",\"params\":");
                    JsonValue(sw, resp.Parameters, 0);
                }
                else
                {
                    unknown++;
                    sw.Write(",\"raw\":");
                    JsonString(sw, BitConverter.ToString(msg).Replace("-", "").ToLowerInvariant());
                }

                sw.WriteLine("}");
                ok++;
            }
            catch
            {
                fail++;
            }
        }

        sw.Flush();
        Console.WriteLine($"ok={ok} fail={fail} unknown={unknown}");
        Console.Write("events: ");
        foreach (var kv in codes) Console.Write($"{kv.Key}:{kv.Value} ");
        Console.WriteLine();
        Console.Write("ops: ");
        foreach (var kv in ops) Console.Write($"{kv.Key}:{kv.Value} ");
        Console.WriteLine();
    }
}
