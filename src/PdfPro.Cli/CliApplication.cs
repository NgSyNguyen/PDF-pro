using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfPro.Cli.Resources;
using PdfPro.Core.Redaction;
using PdfPro.Core.Security;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Signing;

namespace PdfPro.Cli;

/// <summary>
/// The commands of pdfpro (docs/CLI.md): one per everyday task (merge, split, rotate, compress, ocr, encrypt, decrypt,
/// watermark, stamp, sign, redact, convert), info, list-ops, run (any operation) and run-workflow. Every task goes
/// through the same operations as the app; results on stdout, progress and errors on stderr.
/// </summary>
public static class CliApplication
{
    /// <summary>Environment variables a password can come from instead of the command line (which other users may see).</summary>
    public const string PasswordVariable = "PDFPRO_PASSWORD";

    public const string OwnerPasswordVariable = "PDFPRO_OWNER_PASSWORD";

    public const string UserPasswordVariable = "PDFPRO_USER_PASSWORD";

    public const string PfxPasswordVariable = "PDFPRO_PFX_PASSWORD";

    public static async Task<int> RunAsync(string[] args, CliEnvironment env, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(env);
        var parse = Create(env).Parse(args);
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors)
            {
                env.Error.WriteLine(string.Format(CultureInfo.CurrentCulture, CliStrings.Error_Prefix, error.Message));
            }

            return ExitCodes.Usage;
        }

        try
        {
            return await parse.InvokeAsync(new InvocationConfiguration { Output = env.Output, Error = env.Error, EnableDefaultExceptionHandler = false }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitCodes.Cancelled;
        }
    }

    public static RootCommand Create(CliEnvironment env)
    {
        var common = new Common();
        var root = new RootCommand(CliStrings.RootDescription);
        common.AddTo(root);
        root.Add(ListOps(env));
        root.Add(Info(env, common));
        root.Add(Run(env, common));
        root.Add(RunWorkflow(env, common));
        root.Add(Merge(env, common));
        root.Add(Split(env, common));
        root.Add(Rotate(env, common));
        root.Add(Compress(env, common));
        root.Add(Ocr(env, common));
        root.Add(Encrypt(env, common));
        root.Add(Decrypt(env, common));
        root.Add(Watermark(env, common));
        root.Add(Stamp(env, common));
        root.Add(Sign(env, common));
        root.Add(Redact(env, common));
        root.Add(Convert(env, common));
        return root;
    }

    private static Argument<string[]> Inputs() => new("inputs") { Arity = ArgumentArity.OneOrMore, Description = CliStrings.Argument_Inputs };

    private static Command ListOps(CliEnvironment env)
    {
        var json = new Option<bool>("--json") { Description = CliStrings.Option_Json };
        var command = new Command("list-ops", CliStrings.Command_ListOps) { json };
        command.SetAction(parse =>
        {
            var descriptors = env.Catalog.Describe();
            if (parse.GetValue(json))
            {
                var array = new JsonArray([.. descriptors.Select(d => (JsonNode)new JsonObject
                {
                    ["id"] = d.Id,
                    ["group"] = d.Group,
                    ["kind"] = JsonNamingPolicy.CamelCase.ConvertName(d.Kind.ToString()),
                    ["manyInputs"] = d.ManyInputs,
                    ["writesPdf"] = d.WritesPdf,
                    ["measured"] = d.Measured,
                    ["parameters"] = new JsonArray([.. WorkflowSteps.Fields(env.Catalog.Find(d.Id)!).Select(f => (JsonNode)new JsonObject
                    {
                        ["name"] = f.Name,
                        ["kind"] = JsonNamingPolicy.CamelCase.ConvertName(f.Kind.ToString()),
                        ["required"] = f.Required,
                        ["default"] = f.Default is null ? null : JsonSerializer.SerializeToNode(f.Default, f.Default.GetType(), ParameterSchema.Json),
                        ["minimum"] = f.Minimum,
                        ["maximum"] = f.Maximum,
                        ["choices"] = f.Choices.Count == 0 ? null : new JsonArray([.. f.Choices.Select(c => (JsonNode)JsonValue.Create(c))]),
                        ["secret"] = f.Secret,
                    })]),
                })]);
                env.Output.WriteLine(array.ToJsonString(CliRun.JsonOutput));
                return ExitCodes.Success;
            }

            foreach (var descriptor in descriptors)
            {
                var fields = WorkflowSteps.Fields(env.Catalog.Find(descriptor.Id)!);
                env.Output.WriteLine($"{descriptor.Id,-26} {string.Join(" ", fields.Select(f => f.Required ? f.Name : $"[{f.Name}]"))}");
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static Command Info(CliEnvironment env, Common common)
    {
        var input = new Argument<string>("input") { Description = CliStrings.Argument_Input };
        var command = new Command("info", CliStrings.Command_Info) { input };
        command.SetAction((parse, token) =>
        {
            var options = common.Read(parse, env);
            return InfoCommand.RunAsync(env, parse.GetValue(input)!, options.Password, options.Json, token);
        });
        return command;
    }

    private static Command Run(CliEnvironment env, Common common)
    {
        var operation = new Argument<string>("operation") { Description = CliStrings.Argument_Operation };
        var inputs = Inputs();
        var parameters = new Option<string?>("--params") { Description = CliStrings.Option_Params };
        var command = new Command("run", CliStrings.Command_Run) { operation, inputs, parameters };
        command.SetAction(async (parse, token) =>
        {
            JsonObject json;
            try
            {
                json = ReadJson(env, parse.GetValue(parameters)) ?? [];
            }
            catch (FormatException ex)
            {
                return CliRun.Usage(env, ex.Message);
            }

            return await CliRun.OperationAsync(env, common.Read(parse, env), parse.GetValue(operation)!, json, parse.GetValue(inputs)!, Secrets(env, parse, common, json: null), token).ConfigureAwait(false);
        });
        return command;
    }

    private static Command RunWorkflow(CliEnvironment env, Common common)
    {
        var workflowArgument = new Argument<string>("workflow") { Description = CliStrings.Argument_Workflow };
        var inputs = Inputs();
        var secrets = new Option<string?>("--secrets") { Description = CliStrings.Option_Secrets };
        var command = new Command("run-workflow", CliStrings.Command_RunWorkflow) { workflowArgument, inputs, secrets };
        common.AddSigning(command);
        command.SetAction(async (parse, token) =>
        {
            var name = parse.GetValue(workflowArgument)!;
            Workflow workflow;
            IReadOnlyDictionary<string, JsonObject>? values;
            try
            {
                workflow = LoadWorkflow(env, name);
                values = parse.GetValue(secrets) is { } file ? CliSecrets.Read(await File.ReadAllTextAsync(env.FullPath(file), token).ConfigureAwait(false)) : null;
            }
            catch (Exception ex) when (ex is WorkflowFormatException or FormatException or IOException or UnauthorizedAccessException)
            {
                return CliRun.Usage(env, string.Format(CultureInfo.CurrentCulture, CliStrings.Error_Workflow, name, ex.Message));
            }

            using var provider = Secrets(env, parse, common, values);
            return await CliRun.WorkflowAsync(env, common.Read(parse, env), workflow, parse.GetValue(inputs)!, provider, token).ConfigureAwait(false);
        });
        return command;
    }

    private static Command Merge(CliEnvironment env, Common common)
    {
        var inputs = Inputs();
        var noBookmarks = new Option<bool>("--no-bookmarks") { Description = CliStrings.Option_NoBookmarks };
        var contents = new Option<bool>("--toc") { Description = CliStrings.Option_TableOfContents };
        var command = new Command("merge", CliStrings.Command_Merge) { inputs, noBookmarks, contents };
        command.SetAction((parse, token) =>
        {
            var options = common.Read(parse, env);
            var files = BatchInputs.Expand(parse.GetValue(inputs)!.Select(env.FullPath), options.Recursive, options.Pattern).Select(f => f.Path).ToList();
            var json = new JsonObject
            {
                ["items"] = new JsonArray([.. files.Select(_ => (JsonNode)new JsonObject())]),
                ["keepBookmarks"] = !parse.GetValue(noBookmarks),
                ["bookmarkPerFile"] = !parse.GetValue(noBookmarks),
                ["tableOfContents"] = parse.GetValue(contents),
                ["tableOfContentsHeading"] = CliStrings.Merge_TableOfContents,
            };

            // "-o hợp đồng.pdf" names the file; "-o thư mục" the folder.
            if (options.Out is { } output && output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                json["outputName"] = Path.GetFileNameWithoutExtension(output);
                options = options with { Out = Path.GetDirectoryName(env.FullPath(output)) };
            }

            return CliRun.OperationAsync(env, options with { Recursive = false, Pattern = null }, "organize.merge", json, files, null, token);
        });
        return command;
    }

    private static Command Split(CliEnvironment env, Common common)
    {
        var inputs = Inputs();
        var ranges = new Option<string?>("--ranges") { Description = CliStrings.Option_Ranges };
        var every = new Option<int?>("--every") { Description = CliStrings.Option_Every };
        var bookmarks = new Option<int?>("--bookmarks") { Description = CliStrings.Option_SplitBookmarks };
        var size = new Option<double?>("--size") { Description = CliStrings.Option_SplitSize };
        var blank = new Option<bool>("--blank") { Description = CliStrings.Option_SplitBlank };
        var command = new Command("split", CliStrings.Command_Split) { inputs, ranges, every, bookmarks, size, blank };
        command.SetAction((parse, token) =>
        {
            var json = new JsonObject();
            if (parse.GetValue(ranges) is { } chosen)
            {
                (json["mode"], json["ranges"]) = ("ranges", chosen);
            }
            else if (parse.GetValue(every) is { } pages)
            {
                (json["mode"], json["pagesPerFile"]) = ("everyN", pages);
            }
            else if (parse.GetValue(bookmarks) is { } level)
            {
                (json["mode"], json["bookmarkLevel"]) = ("bookmarks", level);
            }
            else if (parse.GetValue(size) is { } megabytes)
            {
                (json["mode"], json["maxMegabytes"]) = ("size", megabytes);
            }
            else if (parse.GetValue(blank))
            {
                json["mode"] = "blankSeparators";
            }
            else
            {
                return Task.FromResult(CliRun.Usage(env, CliStrings.Error_SplitMode));
            }

            return CliRun.OperationAsync(env, common.Read(parse, env), "organize.split", json, parse.GetValue(inputs)!, null, token);
        });
        return command;
    }

    private static Command Rotate(CliEnvironment env, Common common)
    {
        var inputs = Inputs();
        var degrees = new Option<int>("--degrees") { Description = CliStrings.Option_Degrees, Required = true };
        var pages = new Option<string>("--pages") { Description = CliStrings.Option_Pages, DefaultValueFactory = _ => "all" };
        var command = new Command("rotate", CliStrings.Command_Rotate) { inputs, degrees, pages };
        command.SetAction((parse, token) => CliRun.OperationAsync(
            env, common.Read(parse, env), "organize.rotate", new JsonObject { ["pages"] = parse.GetValue(pages), ["degrees"] = parse.GetValue(degrees) }, parse.GetValue(inputs)!, null, token));
        return command;
    }

    private static Command Compress(CliEnvironment env, Common common)
    {
        var inputs = Inputs();
        var preset = new Option<string>("--preset") { Description = CliStrings.Option_Preset, DefaultValueFactory = _ => "ebook" };
        preset.AcceptOnlyFromAmong("screen", "ebook", "print", "archive", "custom");
        var dpi = new Option<int?>("--dpi") { Description = CliStrings.Option_Dpi };
        var quality = new Option<int?>("--quality") { Description = CliStrings.Option_Quality };
        var command = new Command("compress", CliStrings.Command_Compress) { inputs, preset, dpi, quality };
        command.SetAction((parse, token) =>
        {
            var json = new JsonObject { ["preset"] = parse.GetValue(preset) };
            if (parse.GetValue(dpi) is { } resolution)
            {
                (json["preset"], json["dpi"]) = ("custom", resolution);
            }

            if (parse.GetValue(quality) is { } jpeg)
            {
                (json["preset"], json["jpegQuality"]) = ("custom", jpeg);
            }

            return CliRun.OperationAsync(env, common.Read(parse, env), "optimize.compress", json, parse.GetValue(inputs)!, null, token);
        });
        return command;
    }

    private static Command Ocr(CliEnvironment env, Common common)
    {
        var inputs = Inputs();
        var languages = new Option<string>("--lang") { Description = CliStrings.Option_Languages, DefaultValueFactory = _ => WorkflowRunner.DefaultLanguage };
        var redo = new Option<bool>("--redo") { Description = CliStrings.Option_Redo };
        var all = new Option<bool>("--all-pages") { Description = CliStrings.Option_AllPages };
        var straighten = new Option<bool>("--straighten") { Description = CliStrings.Option_Straighten };
        var command = new Command("ocr", CliStrings.Command_Ocr) { inputs, languages, redo, all, straighten };
        command.SetAction((parse, token) =>
        {
            var json = new JsonObject
            {
                ["languages"] = new JsonArray([.. parse.GetValue(languages)!.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => (JsonNode)JsonValue.Create(l))]),
                ["existingText"] = parse.GetValue(all) ? "all" : parse.GetValue(redo) ? "replace" : "skip",
                ["straightenPages"] = parse.GetValue(straighten),
            };
            return CliRun.OperationAsync(env, common.Read(parse, env), "ocr.run", json, parse.GetValue(inputs)!, null, token);
        });
        return command;
    }

    private static Command Encrypt(CliEnvironment env, Common common)
    {
        var inputs = Inputs();
        var owner = new Option<string?>("--owner-password") { Description = CliStrings.Option_OwnerPassword };
        var user = new Option<string?>("--user-password") { Description = CliStrings.Option_UserPassword };
        var aes128 = new Option<bool>("--aes128") { Description = CliStrings.Option_Aes128 };
        var allow = new Option<string?>("--allow") { Description = CliStrings.Option_Allow };
        var command = new Command("encrypt", CliStrings.Command_Encrypt) { inputs, owner, user, aes128, allow };
        command.SetAction((parse, token) =>
        {
            var ownerPassword = parse.GetValue(owner) ?? env.Variables(OwnerPasswordVariable);
            if (string.IsNullOrEmpty(ownerPassword))
            {
                return Task.FromResult(CliRun.Usage(env, CliStrings.Error_OwnerPassword));
            }

            var permissions = DocumentPermissions.All;
            if (parse.GetValue(allow) is { } list)
            {
                permissions = DocumentPermissions.None;
                foreach (var name in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Enum.TryParse<DocumentPermissions>(name, ignoreCase: true, out var permission) || int.TryParse(name, out _))
                    {
                        return Task.FromResult(CliRun.Usage(env, string.Format(CultureInfo.CurrentCulture, CliStrings.Error_Permission, name)));
                    }

                    permissions |= permission;
                }
            }

            var stored = new JsonObject
            {
                ["security"] = new JsonObject
                {
                    ["algorithm"] = parse.GetValue(aes128) ? "aes128" : "aes256",
                    ["permissions"] = JsonSerializer.SerializeToNode(permissions, ParameterSchema.Json),
                },
            };
            var secret = new JsonObject { ["security"] = new JsonObject { ["ownerPassword"] = ownerPassword, ["userPassword"] = parse.GetValue(user) ?? env.Variables(UserPasswordVariable) } };
            return CliRun.OperationAsync(env, common.Read(parse, env), "security.encrypt", stored, parse.GetValue(inputs)!, new WorkflowSecretValues(new Dictionary<string, JsonObject> { ["security.encrypt"] = secret }), token);
        });
        return command;
    }

    private static Command Decrypt(CliEnvironment env, Common common)
    {
        var inputs = Inputs();
        var command = new Command("decrypt", CliStrings.Command_Decrypt) { inputs };
        command.SetAction((parse, token) =>
        {
            var options = common.Read(parse, env);
            return options.Password is null
                ? Task.FromResult(CliRun.Usage(env, CliStrings.Error_DecryptPassword))
                : CliRun.OperationAsync(env, options, "security.decrypt", [], parse.GetValue(inputs)!, null, token);
        });
        return command;
    }

    private static Command Watermark(CliEnvironment env, Common common)
    {
        var inputs = Inputs();
        var text = new Option<string>("--text") { Description = CliStrings.Option_WatermarkText, Required = true };
        var opacity = new Option<double>("--opacity") { Description = CliStrings.Option_Opacity, DefaultValueFactory = _ => 0.3 };
        var rotation = new Option<double>("--rotation") { Description = CliStrings.Option_Rotation, DefaultValueFactory = _ => 45 };
        var pages = new Option<string?>("--pages") { Description = CliStrings.Option_Pages };
        var behind = new Option<bool>("--behind") { Description = CliStrings.Option_Behind };
        var command = new Command("watermark", CliStrings.Command_Watermark) { inputs, text, opacity, rotation, pages, behind };
        command.SetAction((parse, token) => CliRun.OperationAsync(
            env,
            common.Read(parse, env),
            "stamp.watermark",
            new JsonObject { ["text"] = parse.GetValue(text), ["opacity"] = parse.GetValue(opacity), ["rotation"] = parse.GetValue(rotation), ["pages"] = parse.GetValue(pages), ["behindContent"] = parse.GetValue(behind) },
            parse.GetValue(inputs)!,
            null,
            token));
        return command;
    }

    private static Command Stamp(CliEnvironment env, Common common)
    {
        var inputs = Inputs();
        var numbers = new Option<string?>("--page-numbers") { Description = CliStrings.Option_PageNumbers };
        var header = new Option<string?>("--header") { Description = CliStrings.Option_Header };
        var footer = new Option<string?>("--footer") { Description = CliStrings.Option_Footer };
        var bates = new Option<string?>("--bates") { Description = CliStrings.Option_Bates };
        var digits = new Option<int>("--digits") { Description = CliStrings.Option_Digits, DefaultValueFactory = _ => 6 };
        var start = new Option<long>("--start") { Description = CliStrings.Option_Start, DefaultValueFactory = _ => 1 };
        var command = new Command("stamp", CliStrings.Command_Stamp) { inputs, numbers, header, footer, bates, digits, start };
        command.SetAction((parse, token) =>
        {
            var options = common.Read(parse, env);
            var files = parse.GetValue(inputs)!;
            if (parse.GetValue(bates) is { } prefix)
            {
                return CliRun.OperationAsync(env, options, "stamp.bates", new JsonObject { ["prefix"] = prefix, ["digits"] = parse.GetValue(digits), ["startNumber"] = parse.GetValue(start), ["overwrite"] = options.Overwrite }, files, null, token);
            }

            if (parse.GetValue(numbers) is { } pattern)
            {
                return CliRun.OperationAsync(env, options, "stamp.pageNumbers", new JsonObject { ["pattern"] = pattern }, files, null, token);
            }

            if (parse.GetValue(header) is not null || parse.GetValue(footer) is not null)
            {
                return CliRun.OperationAsync(env, options, "stamp.headerFooter", new JsonObject { ["topCenter"] = parse.GetValue(header), ["bottomCenter"] = parse.GetValue(footer) }, files, null, token);
            }

            return Task.FromResult(CliRun.Usage(env, CliStrings.Error_StampKind));
        });
        return command;
    }

    private static Command Sign(CliEnvironment env, Common common)
    {
        var inputs = Inputs();
        var reason = new Option<string?>("--reason") { Description = CliStrings.Option_Reason };
        var location = new Option<string?>("--location") { Description = CliStrings.Option_Location };
        var contact = new Option<string?>("--contact") { Description = CliStrings.Option_Contact };
        var timestamp = new Option<Uri?>("--tsa") { Description = CliStrings.Option_Tsa };
        var pades = new Option<bool>("--pades") { Description = CliStrings.Option_Pades };
        var longTerm = new Option<bool>("--ltv") { Description = CliStrings.Option_Ltv };
        var command = new Command("sign", CliStrings.Command_Sign) { inputs, reason, location, contact, timestamp, pades, longTerm };
        common.AddSigning(command);
        command.SetAction(async (parse, token) =>
        {
            if (parse.GetValue(common.Pfx) is null)
            {
                return CliRun.Usage(env, CliStrings.Error_Pfx);
            }

            var request = new JsonObject
            {
                ["fieldName"] = string.Empty,
                ["reason"] = parse.GetValue(reason),
                ["location"] = parse.GetValue(location),
                ["contactInfo"] = parse.GetValue(contact),
                ["timestampServer"] = parse.GetValue(timestamp)?.AbsoluteUri,
                ["pades"] = parse.GetValue(pades),
                ["longTerm"] = parse.GetValue(longTerm),
            };
            using var secrets = Secrets(env, parse, common, null);
            return await CliRun.OperationAsync(env, common.Read(parse, env), SignOperation.OperationId, new JsonObject { ["request"] = request }, parse.GetValue(inputs)!, secrets, token).ConfigureAwait(false);
        });
        return command;
    }

    private static Command Redact(CliEnvironment env, Common common)
    {
        var inputs = Inputs();
        var patterns = new Option<string?>("--pattern") { Description = CliStrings.Option_RedactPatterns };
        var expression = new Option<string?>("--regex") { Description = CliStrings.Option_Regex };
        var label = new Option<string?>("--label") { Description = CliStrings.Option_Label };
        var keepText = new Option<bool>("--keep-text") { Description = CliStrings.Option_KeepText };
        var command = new Command("redact", CliStrings.Command_Redact) { inputs, patterns, expression, label, keepText };
        command.SetAction((parse, token) =>
        {
            var chosen = new List<RedactionPattern>();
            foreach (var name in (parse.GetValue(patterns) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                RedactionPattern? pattern = name.ToLowerInvariant() switch
                {
                    "cccd" or "id" or "nationalid" => RedactionPattern.NationalId,
                    "phone" or "sdt" => RedactionPattern.Phone,
                    "email" => RedactionPattern.Email,
                    "account" or "stk" or "bankaccount" => RedactionPattern.BankAccount,
                    "date" => RedactionPattern.Date,
                    _ => null,
                };
                if (pattern is null)
                {
                    return Task.FromResult(CliRun.Usage(env, string.Format(CultureInfo.CurrentCulture, CliStrings.Error_RedactPattern, name)));
                }

                chosen.Add(pattern.Value);
            }

            if (parse.GetValue(expression) is { Length: > 0 })
            {
                chosen.Add(RedactionPattern.Custom);
            }

            if (chosen.Count == 0)
            {
                return Task.FromResult(CliRun.Usage(env, CliStrings.Error_RedactNothing));
            }

            var measure = new RedactPatternsMeasure(chosen, parse.GetValue(expression), Label: parse.GetValue(label), Mode: parse.GetValue(keepText) ? Operations.Security.RedactionMode.Vector : Operations.Security.RedactionMode.Raster);
            return CliRun.OperationAsync(env, common.Read(parse, env), "security.redact", ParameterSchema.ToJson(measure), parse.GetValue(inputs)!, null, token);
        });
        return command;
    }

    private static Command Convert(CliEnvironment env, Common common)
    {
        var inputs = Inputs();
        var to = new Option<string>("--to") { Description = CliStrings.Option_To, Required = true };
        to.AcceptOnlyFromAmong("pdf", "png", "jpeg", "tiff", "text", "markdown", "word", "docx", "pdfa", "pdfa1b", "grayscale", "web");
        var dpi = new Option<int?>("--dpi") { Description = CliStrings.Option_Dpi };
        var command = new Command("convert", CliStrings.Command_Convert) { inputs, to, dpi };
        command.SetAction((parse, token) =>
        {
            var options = common.Read(parse, env);
            var files = parse.GetValue(inputs)!;
            var resolution = parse.GetValue(dpi);
            JsonObject Images(string format) => resolution is { } chosen ? new JsonObject { ["format"] = format, ["dpi"] = chosen } : new JsonObject { ["format"] = format };
            var (operation, json) = parse.GetValue(to) switch
            {
                "pdf" when files.All(IsPicture) => ("convert.fromImages", new JsonObject()),
                "pdf" => ("convert.officeToPdf", new JsonObject()),
                "png" => ("convert.toImages", Images("png")),
                "jpeg" => ("convert.toImages", Images("jpeg")),
                "tiff" => ("convert.toImages", Images("tiff")),
                "text" => ("convert.toText", new JsonObject { ["format"] = "text" }),
                "markdown" => ("convert.toText", new JsonObject { ["format"] = "markdown" }),
                "word" or "docx" => ("convert.toWord", new JsonObject()),
                "pdfa" => ("convert.toPdfA", new JsonObject { ["level"] = "a2B" }),
                "pdfa1b" => ("convert.toPdfA", new JsonObject { ["level"] = "a1B" }),
                "grayscale" => ("optimize.grayscale", new JsonObject()),
                _ => ("optimize.linearize", new JsonObject()),
            };
            if (operation == "convert.fromImages" && options.Out is { } output && output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                // The PDF is named after the first picture: write it there, then give it the name asked for.
                return FromImagesAsync(env, options, files, env.FullPath(output), token);
            }

            return CliRun.OperationAsync(env, options, operation, json, files, null, token);
        });
        return command;
    }

    private static async Task<int> FromImagesAsync(CliEnvironment env, CommonOptions options, IReadOnlyList<string> files, string output, CancellationToken token)
    {
        var folder = Path.Combine(env.WorkingFolder, $"images-{Guid.NewGuid():N}");
        var quiet = new StringWriter();
        try
        {
            var code = await CliRun.OperationAsync(env with { Output = quiet }, options with { Out = folder, Json = false }, "convert.fromImages", [], files, null, token).ConfigureAwait(false);
            var written = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.pdf").SingleOrDefault() : null;
            if (code != ExitCodes.Success || written is null)
            {
                return code == ExitCodes.Success ? ExitCodes.Failed : code;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (File.Exists(output) && !options.Overwrite)
            {
                return CliRun.Usage(env, string.Format(CultureInfo.CurrentCulture, CliStrings.Error_Exists, output));
            }

            File.Move(written, output, overwrite: true);
            CliRun.Report(env, options, [new FileOutcome(string.Join("; ", files), BatchItemStatus.Done, [output], null)]);
            return ExitCodes.Success;
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private static bool IsPicture(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or ".bmp" or ".gif" or ".webp";

    /// <summary>A workflow file, or a built-in workflow by its id ("digitize", "publish", "print").</summary>
    private static Workflow LoadWorkflow(CliEnvironment env, string name)
    {
        var path = env.FullPath(name);
        if (!File.Exists(path) && WorkflowTemplates.Ids.Contains(name.ToLowerInvariant()))
        {
            var id = name.ToLowerInvariant();
            var title = id switch
            {
                WorkflowTemplates.Digitize => CliStrings.Template_Digitize,
                WorkflowTemplates.Publish => CliStrings.Template_Publish,
                _ => CliStrings.Template_Print,
            };
            return WorkflowTemplates.Create(id, new WorkflowTemplateTexts(title, CliStrings.Template_Watermark, CliStrings.Template_PageNumbers));
        }

        return WorkflowFile.Load(path);
    }

    private static JsonObject? ReadJson(CliEnvironment env, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var json = text.TrimStart().StartsWith('{') ? text : File.ReadAllText(env.FullPath(text));
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? throw new FormatException(CliStrings.Error_ParamsJson);
        }
        catch (JsonException ex)
        {
            throw new FormatException(CliStrings.Error_ParamsJson, ex);
        }
    }

    private static CliSecrets Secrets(CliEnvironment env, ParseResult parse, Common common, IReadOnlyDictionary<string, JsonObject>? json)
    {
        SigningCertificate? certificate = null;
        if (parse.GetValue(common.Pfx) is { } pfx)
        {
            var visible = parse.GetValue(common.Visible) switch
            {
                "first" => SignaturePage.First,
                "last" => SignaturePage.Last,
                _ => (SignaturePage?)null,
            };
            certificate = new SigningCertificate(env.FullPath(pfx), parse.GetValue(common.PfxPassword) ?? env.Variables(PfxPasswordVariable), visible);
        }

        return new CliSecrets(json ?? new Dictionary<string, JsonObject>(), certificate);
    }

    /// <summary>The options every command takes, and the signing ones.</summary>
    private sealed class Common
    {
        public Option<string?> Out { get; } = new("--out", "-o") { Description = CliStrings.Option_Out, Recursive = true };

        public Option<bool> Overwrite { get; } = new("--overwrite") { Description = CliStrings.Option_Overwrite, Recursive = true };

        public Option<bool> InPlace { get; } = new("--in-place") { Description = CliStrings.Option_InPlace, Recursive = true };

        public Option<bool> Recursive { get; } = new("--recursive", "-r") { Description = CliStrings.Option_Recursive, Recursive = true };

        public Option<string?> Pattern { get; } = new("--pattern") { Description = CliStrings.Option_Pattern, Recursive = true };

        public Option<int> Parallel { get; } = new("--parallel") { Description = CliStrings.Option_Parallel, Recursive = true };

        public Option<string?> Password { get; } = new("--password") { Description = CliStrings.Option_Password, Recursive = true };

        public Option<bool> Json { get; } = new("--json") { Description = CliStrings.Option_Json, Recursive = true };

        public Option<bool> Quiet { get; } = new("--quiet", "-q") { Description = CliStrings.Option_Quiet, Recursive = true };

        public Option<string?> Log { get; } = new("--log") { Description = CliStrings.Option_Log, Recursive = true };

        public Option<string[]> Parameters { get; } = new("--param", "-p") { Description = CliStrings.Option_Param, Recursive = true };

        public Option<string?> Pfx { get; } = new("--pfx") { Description = CliStrings.Option_Pfx };

        public Option<string?> PfxPassword { get; } = new("--pfx-password") { Description = CliStrings.Option_PfxPassword };

        public Option<string?> Visible { get; } = new("--visible") { Description = CliStrings.Option_Visible };

        public void AddTo(RootCommand root)
        {
            root.Add(Out);
            root.Add(Overwrite);
            root.Add(InPlace);
            root.Add(Recursive);
            root.Add(Pattern);
            root.Add(Parallel);
            root.Add(Password);
            root.Add(Json);
            root.Add(Quiet);
            root.Add(Log);
            root.Add(Parameters);
            Visible.AcceptOnlyFromAmong("first", "last");
        }

        public void AddSigning(Command command)
        {
            command.Add(Pfx);
            command.Add(PfxPassword);
            command.Add(Visible);
        }

        public CommonOptions Read(ParseResult parse, CliEnvironment env) => new(
            parse.GetValue(Out),
            parse.GetValue(Overwrite),
            parse.GetValue(InPlace),
            parse.GetValue(Recursive),
            parse.GetValue(Pattern),
            parse.GetValue(Parallel),
            parse.GetValue(Password) ?? env.Variables(PasswordVariable),
            parse.GetValue(Json),
            parse.GetValue(Quiet),
            parse.GetValue(Log),
            parse.GetValue(Parameters) ?? []);
    }
}
