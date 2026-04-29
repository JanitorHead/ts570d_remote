using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;

namespace TS570_Remote;

/// <summary>
/// OmniRig CAT sender (COM). Uses SendCustomCommand to send raw CAT strings.
/// </summary>
internal sealed class OmniRigCatClient : IDisposable
{
    internal readonly record struct RigSnapshot(
        int Status,
        int FreqA,
        int FreqB,
        int Mode,
        int Vfo,
        int Split,
        int Rit,
        int Xit,
        int Tx,
        int RitOffset);

    private readonly dynamic _omniRig;
    private readonly dynamic _rig;
    private readonly object _customLock = new();
    private readonly Dictionary<string, string> _lastCustomReplies = new(StringComparer.OrdinalIgnoreCase);
    private string _lastRequestedCustomKey = string.Empty;
    private int _rigNumber;
    private bool _customReplyHooked;
    private string _customReplyDebug = "hook:pending";

    public OmniRigCatClient(int rigNumber)
    {
        // OmniRig COM ProgID (see OmniRig COM examples / sources)
        Type t = Type.GetTypeFromProgID("OmniRig.OmniRigX", throwOnError: true)!;
        _omniRig = Activator.CreateInstance(t)!;

        // rigNumber is typically 1..3 exposed as properties Rig1, Rig2, ...
        _rig = GetRigProperty(_omniRig, rigNumber);
        _rigNumber = rigNumber;
        TryHookCustomReplyEvent();
    }

    private static dynamic GetRigProperty(dynamic omniRig, int rigNumber)
    {
        if (rigNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(rigNumber));

        // Use COM late-binding directly. Reflection on RCW metadata may miss IDispatch members.
        try
        {
            return rigNumber switch
            {
                1 => omniRig.Rig1,
                2 => omniRig.Rig2,
                _ => throw new InvalidOperationException($"Unsupported OmniRig rig number: {rigNumber}.")
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to access OmniRig Rig{rigNumber}: {ex.Message}", ex);
        }
    }

    public void SendCustomCommand(string command, int replyLength = 0, string replyEnd = "")
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        try
        {
            // ReplyLength=0 for write-only commands.
            _rig.SendCustomCommand(command, replyLength, replyEnd);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OmniRig SendCustomCommand failed: {ex.Message}");
        }
    }

    public bool TryReadCustomCommand(string command, int replyLength, string replyEnd, out string reply)
    {
        reply = string.Empty;
        if (string.IsNullOrWhiteSpace(command))
            return false;

        try
        {
            // OmniRig custom replies are asynchronous (CustomReply event).
            _rig.SendCustomCommand(command, replyLength, replyEnd);
            string key = GetCustomCommandKey(command);
            lock (_customLock)
            {
                if (!_lastCustomReplies.TryGetValue(key, out string? cached) || string.IsNullOrWhiteSpace(cached))
                    return false;

                reply = cached;
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OmniRig read custom command failed: {ex.Message}");
            return false;
        }
    }

    public bool TryReadSnapshot(out RigSnapshot snapshot)
    {
        snapshot = default;
        try
        {
            snapshot = new RigSnapshot(
                Status: (int)_rig.Status,
                FreqA: (int)_rig.FreqA,
                FreqB: (int)_rig.FreqB,
                Mode: (int)_rig.Mode,
                Vfo: (int)_rig.Vfo,
                Split: (int)_rig.Split,
                Rit: (int)_rig.Rit,
                Xit: (int)_rig.Xit,
                Tx: (int)_rig.Tx,
                RitOffset: (int)_rig.RitOffset
            );
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OmniRig read snapshot failed: {ex.Message}");
            return false;
        }
    }

    public bool TryReadSignalLevel(out int value)
    {
        value = 0;

        // OmniRig exposes rig values through COM properties depending on driver/ini.
        // Try several common names and keep first valid int.
        string[] candidates =
        {
            "SLevel", "SMeter", "SignalStrength", "Strength", "Level", "SignalLevel", "Meter"
        };

        foreach (string name in candidates)
        {
            if (TryGetIntComPropertyLateBound(name, out int v))
            {
                value = Math.Clamp(v, 0, 255);
                return true;
            }
        }

        return false;
    }

    public void RequestSmeterRead()
    {
        // Single request to avoid CAT queue buildup.
        lock (_customLock)
            _lastRequestedCustomKey = "SM";
        SendCustomCommand("SM;", replyLength: 6, replyEnd: ";");
    }

    public bool TryGetLastSmeterRaw(out int raw, out string debugRaw)
    {
        raw = 0;
        debugRaw = string.Empty;

        string smReply;
        lock (_customLock)
        {
            if (!_lastCustomReplies.TryGetValue("SM", out smReply!))
                return false;
        }

        debugRaw = smReply.Trim();
        // Accept "SM123;", "123", "0123", etc.
        Match m = Regex.Match(smReply, @"SM\s*(\d{1,4})|(\d{1,4})", RegexOptions.IgnoreCase);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int parsed))
        {
            string digits = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;
            if (!int.TryParse(digits, out parsed))
                return false;
        }

        raw = Math.Clamp(parsed, 0, 30);
        return true;
    }

    private bool TryGetIntComPropertyLateBound(string propertyName, out int value)
    {
        value = 0;
        try
        {
            // Must use dynamic late-binding for COM IDispatch members.
            object? raw = propertyName switch
            {
                "SLevel" => _rig.SLevel,
                "SMeter" => _rig.SMeter,
                "SignalStrength" => _rig.SignalStrength,
                "Strength" => _rig.Strength,
                "Level" => _rig.Level,
                "SignalLevel" => _rig.SignalLevel,
                "Meter" => _rig.Meter,
                _ => null
            };

            if (raw is null)
                return false;

            value = raw switch
            {
                int i => i,
                short s => s,
                long l => (int)l,
                byte b => b,
                _ => Convert.ToInt32(raw, CultureInfo.InvariantCulture)
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_customReplyHooked)
            {
                try
                {
                    dynamic omni = _omniRig;
                    omni.CustomReply -= (Action<int, object, object>)OnCustomReply;
                }
                catch
                {
                    // Best effort; RCW cleanup below still releases COM object.
                }
            }

            // Some COM wrappers expose Close/Dispose; be conservative.
            if (_omniRig is not null)
                System.Runtime.InteropServices.Marshal.ReleaseComObject((object)_omniRig);
        }
        catch
        {
            // Ignore dispose errors.
        }
    }

    public string GetCustomReplyDebug()
    {
        lock (_customLock)
            return _customReplyDebug;
    }

    private void TryHookCustomReplyEvent()
    {
        try
        {
            dynamic omni = _omniRig;
            omni.CustomReply += (Action<int, object, object>)OnCustomReply;
            _customReplyHooked = true;
            lock (_customLock)
                _customReplyDebug = "hook:ok";
        }
        catch (Exception ex)
        {
            _customReplyHooked = false;
            lock (_customLock)
                _customReplyDebug = $"hook:fail {ex.Message}";
            Debug.WriteLine($"OmniRig CustomReply hook failed: {ex.Message}");
        }
    }

    private void OnCustomReply(int rigNumber, object command, object reply)
    {
        try
        {
            if (rigNumber != _rigNumber)
                return;

            string cmd = VariantToText(command);
            string rep = VariantToText(reply);
            if (string.IsNullOrWhiteSpace(cmd) || string.IsNullOrWhiteSpace(rep))
            {
                lock (_customLock)
                {
                    _customReplyDebug =
                        $"evt rig={rigNumber} cmdType={command?.GetType().FullName ?? "<null>"} repType={reply?.GetType().FullName ?? "<null>"} cmd='{cmd.Trim()}' rep='{rep.Trim()}'";
                }
                return;
            }

            // Prefer key from reply when possible (more reliable on some COM marshaling paths).
            string key = GetCustomCommandKey(rep);
            if (key.Length < 2 || !char.IsLetter(key[0]) || !char.IsLetter(key[1]))
                key = GetCustomCommandKey(cmd);
            if (key.Length < 2 || !char.IsLetter(key[0]) || !char.IsLetter(key[1]))
            {
                // Some OmniRig paths return just numeric payload; map to last requested custom key.
                lock (_customLock)
                    key = _lastRequestedCustomKey;
            }
            lock (_customLock)
            {
                if (!string.IsNullOrWhiteSpace(key))
                    _lastCustomReplies[key] = rep;
                _customReplyDebug = $"evt rig={rigNumber} key='{key}' cmd='{cmd.Trim()}' rep='{rep.Trim()}'";
            }
        }
        catch (Exception ex)
        {
            lock (_customLock)
                _customReplyDebug = $"evt:err {ex.Message}";
            Debug.WriteLine($"OmniRig CustomReply processing failed: {ex.Message}");
        }
    }

    private static string GetCustomCommandKey(string command)
    {
        string c = command.Trim().ToUpperInvariant();
        if (c.Length >= 2 && char.IsLetter(c[0]) && char.IsLetter(c[1]))
            return c[..2];
        return c;
    }

    private static string VariantToText(object value)
    {
        if (value is null)
            return string.Empty;
        if (value is string s)
            return s;
        if (value is byte[] b)
            return Encoding.ASCII.GetString(b);
        if (value is char c)
            return c.ToString();
        if (value is Array arr)
        {
            List<byte> bytes = new();
            foreach (object? item in arr)
            {
                switch (item)
                {
                    case byte bb:
                        bytes.Add(bb);
                        break;
                    case sbyte sb:
                        bytes.Add((byte)sb);
                        break;
                    case short ss:
                        bytes.Add((byte)(ss & 0xFF));
                        break;
                    case ushort us:
                        bytes.Add((byte)(us & 0xFF));
                        break;
                    case int ii:
                        bytes.Add((byte)(ii & 0xFF));
                        break;
                    case uint ui:
                        bytes.Add((byte)(ui & 0xFF));
                        break;
                    case long ll:
                        bytes.Add((byte)(ll & 0xFF));
                        break;
                    case char ch:
                        bytes.Add((byte)ch);
                        break;
                    case string str when str.Length > 0:
                        bytes.Add((byte)str[0]);
                        break;
                }
            }

            if (bytes.Count == 0)
                return string.Empty;

            // Some SAFEARRAY payloads are UTF-16-ish (every second byte is 0). Strip NUL bytes first.
            if (bytes.Count > 2 && bytes.Count % 2 == 0)
            {
                int nulCount = 0;
                for (int i = 1; i < bytes.Count; i += 2)
                    if (bytes[i] == 0) nulCount++;
                if (nulCount >= (bytes.Count / 4))
                    bytes = bytes.Where((_, idx) => idx % 2 == 0).ToList();
            }

            bool printable = bytes.All(x => x == 9 || x == 10 || x == 13 || (x >= 32 && x <= 126));
            return printable
                ? Encoding.ASCII.GetString(bytes.ToArray())
                : BitConverter.ToString(bytes.ToArray()).Replace("-", "");
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

