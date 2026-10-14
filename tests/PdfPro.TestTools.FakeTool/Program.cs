using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

// Fake external tool for IExternalToolRunner tests. Usage: FakeTool <mode> [arguments]
//   echo <args...>              print the remaining arguments as a JSON array (UTF-8)
//   exit <code> [stderr text]   write the text to stderr and exit with the code
//   sleep <ms>                  sleep, then exit 0
//   spawn-sleep <ms> <pidFile>  start a child "sleep <ms>", write its pid to pidFile, then sleep
//   flood <chars>               write that many characters to stdout
//   stdin                       read stdin to the end and print how many characters were read
//   env <name>                  print the value of an environment variable
//   cwd                         print the current directory
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

if (args.Length == 0)
{
    Console.Error.WriteLine("missing mode");
    return 64;
}

var rest = args[1..];
switch (args[0])
{
    case "echo":
        Console.Write(JsonSerializer.Serialize(rest));
        return 0;

    case "exit":
        if (rest.Length > 1)
        {
            Console.Error.Write(rest[1]);
        }

        return int.Parse(rest[0], CultureInfo.InvariantCulture);

    case "sleep":
        Thread.Sleep(int.Parse(rest[0], CultureInfo.InvariantCulture));
        return 0;

    case "spawn-sleep":
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("sleep");
            start.ArgumentList.Add(rest[0]);
            using var child = Process.Start(start)!;
            File.WriteAllText(rest[1], child.Id.ToString(CultureInfo.InvariantCulture));
            Thread.Sleep(int.Parse(rest[0], CultureInfo.InvariantCulture));
            return 0;
        }

    case "flood":
        Console.Write(new string('x', int.Parse(rest[0], CultureInfo.InvariantCulture)));
        return 0;

    case "stdin":
        Console.Write(Console.In.ReadToEnd().Length.ToString(CultureInfo.InvariantCulture));
        return 0;

    case "env":
        Console.Write(Environment.GetEnvironmentVariable(rest[0]) ?? "<null>");
        return 0;

    case "cwd":
        Console.Write(Environment.CurrentDirectory);
        return 0;

    default:
        Console.Error.WriteLine($"unknown mode {args[0]}");
        return 64;
}
