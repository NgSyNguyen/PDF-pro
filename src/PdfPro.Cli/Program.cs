using System.CommandLine;
using System.Text;
using PdfPro.Cli.Resources;

// Vietnamese help text must survive the console code page.
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// RootCommand adds --help and --version automatically. Operation commands (merge, split, ocr…) arrive in Phase 12.
var root = new RootCommand(CliStrings.RootDescription);
return root.Parse(args).Invoke();
