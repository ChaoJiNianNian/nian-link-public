// A deterministic child process fixture. It has no native display or pairing dependencies.
var index = Array.IndexOf(args, "--input");
var input = index >= 0 && index + 1 < args.Length ? args[index + 1] : "0";
if (input == "1") { await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
if (input == "2") { Console.Write(new string('x', 17000)); return 0; }
if (input == "4") { Console.Write("not-json"); return 0; }
Console.Write("{\"ok\":true,\"set\":{\"commandAccepted\":true,\"nativeError\":0,\"error\":null}}");
return 0;
