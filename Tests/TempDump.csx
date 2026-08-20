using System;
using System.IO;
using Biz.Bizadm.SC2ReplayTrace;

var replayPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay.SC2Replay");
await using var stream = File.OpenRead(replayPath);
var raw = await Sc2ReplayParser.ParseRawAsync(stream);
foreach (var kv in raw.Files)
    Console.WriteLine($"{kv.Key} len={kv.Value.Length} first8={BitConverter.ToString(kv.Value[..Math.Min(8, kv.Value.Length)])}");
