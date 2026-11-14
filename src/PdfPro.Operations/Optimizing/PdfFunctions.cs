using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Optimizing;

/// <summary>
/// A PDF function (ISO 32000 7.10): type 0 (sampled), 2 (exponential), 3 (stitching) or 4 (PostScript calculator), or
/// an array of one-output functions taken as one. Inputs are clipped to the domain, outputs to the range.
/// </summary>
internal abstract class PdfFunction
{
    private const int MaxDepth = 8;

    private readonly double[] _domain;
    private readonly double[]? _range;

    protected PdfFunction(double[] domain, double[]? range)
    {
        _domain = domain;
        _range = range;
    }

    public int Inputs => _domain.Length / 2;

    public abstract int Outputs { get; }

    /// <summary>The input ranges: minimum and maximum of each input in turn.</summary>
    public double[] Domain => _domain;

    /// <summary>The function a dictionary, stream or array describes; null when it is malformed or of another kind.</summary>
    public static PdfFunction? Read(PdfItem? item) => Read(item, 0);

    /// <exception cref="FormatException">A calculator program fails (stack underflow, unknown operator, runs too long).</exception>
    public double[] Evaluate(ReadOnlySpan<double> inputs)
    {
        var clipped = new double[Inputs];
        for (var i = 0; i < clipped.Length; i++)
        {
            var x = i < inputs.Length ? inputs[i] : _domain[2 * i];
            clipped[i] = Math.Clamp(double.IsFinite(x) ? x : _domain[2 * i], _domain[2 * i], _domain[(2 * i) + 1]);
        }

        var outputs = Compute(clipped);
        if (_range is not null)
        {
            for (var j = 0; j < outputs.Length && (2 * j) + 1 < _range.Length; j++)
            {
                outputs[j] = Math.Clamp(double.IsFinite(outputs[j]) ? outputs[j] : _range[2 * j], _range[2 * j], _range[(2 * j) + 1]);
            }
        }

        return outputs;
    }

    /// <summary>The numbers of an array entry, or null when missing or not all numbers.</summary>
    internal static double[]? Numbers(PdfItem? item)
    {
        if (AsArray(item) is not { } array)
        {
            return null;
        }

        var numbers = new double[array.Elements.Count];
        for (var i = 0; i < numbers.Length; i++)
        {
            if (Number(array.Elements[i]) is not { } value)
            {
                return null;
            }

            numbers[i] = value;
        }

        return numbers;
    }

    internal static double? Number(PdfItem? item) => Resolve(item) switch
    {
        PdfInteger integer => integer.Value,
        PdfLongInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => null,
    };

    protected static double Interpolate(double x, double xMin, double xMax, double yMin, double yMax)
        => xMax == xMin ? yMin : yMin + ((x - xMin) * (yMax - yMin) / (xMax - xMin));

    protected abstract double[] Compute(double[] inputs);

    private static PdfFunction? Read(PdfItem? item, int depth)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        if (AsArray(item) is { } array)
        {
            var parts = array.Elements.Select(e => Read(e, depth + 1)).ToArray();
            return parts.Length > 0 && parts.All(p => p is { Outputs: 1 }) && parts.Select(p => p!.Inputs).Distinct().Count() == 1
                ? new Combined(parts!)
                : null;
        }

        if (AsDictionary(item) is not { } function || Numbers(function.Elements["/Domain"]) is not { Length: >= 2 } domain || domain.Length % 2 != 0)
        {
            return null;
        }

        var range = Numbers(function.Elements["/Range"]);
        try
        {
            return StreamData.Integer(function, "/FunctionType") switch
            {
                0 => Sampled.Read(function, domain, range),
                2 => Exponential.Read(function, domain, range),
                3 => Stitching.Read(function, domain, range, depth),
                4 => Calculator.Read(function, domain, range),
                _ => null,
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Several one-output functions of the same inputs: output j is function j's.</summary>
    private sealed class Combined(PdfFunction[] parts) : PdfFunction(parts[0]._domain, null)
    {
        public override int Outputs => parts.Length;

        protected override double[] Compute(double[] inputs) => [.. parts.Select(p => p.Evaluate(inputs)[0])];
    }

    /// <summary>Type 0: a table of samples, interpolated (multilinear; cubic order is taken as linear).</summary>
    private sealed class Sampled : PdfFunction
    {
        private const long MaxSamples = 16_000_000;

        private readonly int[] _size;
        private readonly int _bits;
        private readonly double[] _encode;
        private readonly double[] _decode;
        private readonly byte[] _data;
        private readonly int _outputs;

        private Sampled(double[] domain, double[] range, int[] size, int bits, double[] encode, double[] decode, byte[] data)
            : base(domain, range)
        {
            (_size, _bits, _encode, _decode, _data, _outputs) = (size, bits, encode, decode, data, range.Length / 2);
        }

        public override int Outputs => _outputs;

        public static Sampled? Read(PdfDictionary function, double[] domain, double[]? range)
        {
            var inputs = domain.Length / 2;
            if (range is not { Length: >= 2 } || range.Length % 2 != 0 || Numbers(function.Elements["/Size"]) is not { } sizes || sizes.Length != inputs
                || sizes.Any(s => s < 1 || s != Math.Floor(s)) || StreamData.Integer(function, "/BitsPerSample") is not { } bits || bits is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32)
                || function.Stream is null || StreamData.Decode(function) is not { } data)
            {
                return null;
            }

            var size = sizes.Select(s => (int)s).ToArray();
            var outputs = range.Length / 2;
            var count = size.Aggregate(1L, (a, s) => a * s) * outputs;
            if (count > MaxSamples || data.LongLength * 8 < count * bits)
            {
                return null;
            }

            var encode = Numbers(function.Elements["/Encode"]) is { } e && e.Length == 2 * inputs ? e : [.. size.SelectMany(s => new double[] { 0, s - 1 })];
            var decode = Numbers(function.Elements["/Decode"]) is { } d && d.Length == 2 * outputs ? d : range;
            return new Sampled(domain, range, size, bits, encode, decode, data);
        }

        protected override double[] Compute(double[] inputs)
        {
            var m = inputs.Length;
            var low = new int[m];
            var fraction = new double[m];
            for (var i = 0; i < m; i++)
            {
                var e = Math.Clamp(Interpolate(inputs[i], _domain[2 * i], _domain[(2 * i) + 1], _encode[2 * i], _encode[(2 * i) + 1]), 0, _size[i] - 1);
                low[i] = Math.Min((int)Math.Floor(e), Math.Max(0, _size[i] - 2));
                fraction[i] = _size[i] == 1 ? 0 : e - low[i];
            }

            var max = Math.Pow(2, _bits) - 1;
            var outputs = new double[_outputs];
            for (var corner = 0; corner < 1 << m; corner++)
            {
                var weight = 1.0;
                var index = 0L;
                var stride = 1L;
                for (var i = 0; i < m; i++)
                {
                    var high = (corner >> i & 1) == 1;
                    weight *= high ? fraction[i] : 1 - fraction[i];
                    index += Math.Min(low[i] + (high ? 1 : 0), _size[i] - 1) * stride;
                    stride *= _size[i];
                }

                if (weight == 0)
                {
                    continue;
                }

                for (var j = 0; j < _outputs; j++)
                {
                    outputs[j] += weight * Sample((index * _outputs) + j);
                }
            }

            for (var j = 0; j < _outputs; j++)
            {
                outputs[j] = Interpolate(outputs[j], 0, max, _decode[2 * j], _decode[(2 * j) + 1]);
            }

            return outputs;
        }

        /// <summary>The sample at <paramref name="position"/>, most significant bit first.</summary>
        private double Sample(long position)
        {
            var bit = position * _bits;
            var value = 0L;
            for (var read = 0; read < _bits;)
            {
                var b = _data[bit >> 3];
                var offset = (int)(bit & 7);
                var take = Math.Min(8 - offset, _bits - read);
                value = (value << take) | (uint)((b >> (8 - offset - take)) & ((1 << take) - 1));
                read += take;
                bit += take;
            }

            return value;
        }
    }

    /// <summary>Type 2: C0 + x^N × (C1 − C0).</summary>
    private sealed class Exponential(double[] domain, double[]? range, double[] c0, double[] c1, double n) : PdfFunction(domain, range)
    {
        public override int Outputs => c0.Length;

        public static Exponential? Read(PdfDictionary function, double[] domain, double[]? range)
        {
            var c0 = Numbers(function.Elements["/C0"]) ?? [0];
            var c1 = Numbers(function.Elements["/C1"]) ?? [1];
            return domain.Length == 2 && c0.Length == c1.Length && Number(function.Elements["/N"]) is { } n ? new Exponential(domain, range, c0, c1, n) : null;
        }

        protected override double[] Compute(double[] inputs)
        {
            var power = Math.Pow(inputs[0], n);
            return [.. c0.Select((c, j) => c + (power * (c1[j] - c)))];
        }
    }

    /// <summary>Type 3: one function per subdomain, its input mapped by /Encode.</summary>
    private sealed class Stitching(double[] domain, double[]? range, PdfFunction[] functions, double[] bounds, double[] encode) : PdfFunction(domain, range)
    {
        public override int Outputs => functions[0].Outputs;

        public static Stitching? Read(PdfDictionary function, double[] domain, double[]? range, int depth)
        {
            if (domain.Length != 2 || AsArray(function.Elements["/Functions"]) is not { Elements.Count: > 0 } list)
            {
                return null;
            }

            var functions = list.Elements.Select(e => PdfFunction.Read(e, depth + 1)).ToArray();
            var bounds = Numbers(function.Elements["/Bounds"]) ?? [];
            var encode = Numbers(function.Elements["/Encode"]);
            return functions.All(f => f is { Inputs: 1 }) && functions.Select(f => f!.Outputs).Distinct().Count() == 1
                && bounds.Length == functions.Length - 1 && encode is not null && encode.Length == 2 * functions.Length
                ? new Stitching(domain, range, functions!, bounds, encode)
                : null;
        }

        protected override double[] Compute(double[] inputs)
        {
            var x = inputs[0];
            var k = 0;
            while (k < bounds.Length && x >= bounds[k])
            {
                k++;
            }

            var low = k == 0 ? _domain[0] : bounds[k - 1];
            var high = k == bounds.Length ? _domain[1] : bounds[k];
            return functions[k].Evaluate([Interpolate(x, low, high, encode[2 * k], encode[(2 * k) + 1])]);
        }
    }

    /// <summary>Type 4: a PostScript calculator program (ISO 32000 7.10.5).</summary>
    private sealed class Calculator : PdfFunction
    {
        private const int MaxStack = 100;
        private const int MaxSteps = 100_000;

        private readonly List<object> _program;
        private readonly int _outputs;

        private Calculator(double[] domain, double[] range, List<object> program)
            : base(domain, range)
        {
            _program = program;
            _outputs = range.Length / 2;
        }

        public override int Outputs => _outputs;

        public static Calculator? Read(PdfDictionary function, double[] domain, double[]? range)
        {
            if (range is not { Length: >= 2 } || function.Stream is null || StreamData.Decode(function) is not { } data)
            {
                return null;
            }

            var tokens = Tokens(Encoding.Latin1.GetString(data));
            var at = 0;
            if (tokens.Count == 0 || tokens[0] != "{")
            {
                return null;
            }

            at++;
            var program = Block(tokens, ref at, 0);
            return program is null ? null : new Calculator(domain, range, program);
        }

        protected override double[] Compute(double[] inputs)
        {
            var stack = new List<Value>(inputs.Select(i => new Value(i, false)));
            var steps = 0;
            Run(_program, stack, ref steps);
            if (stack.Count < _outputs)
            {
                throw new FormatException("The calculator left too few values.");
            }

            return [.. stack.Skip(stack.Count - _outputs).Select(v => v.Number)];
        }

        private static List<string> Tokens(string text)
        {
            var tokens = new List<string>();
            var token = new StringBuilder();
            foreach (var c in text)
            {
                if (c is '{' or '}')
                {
                    Flush();
                    tokens.Add(c.ToString());
                }
                else if (char.IsWhiteSpace(c))
                {
                    Flush();
                }
                else
                {
                    token.Append(c);
                }
            }

            Flush();
            return tokens;

            void Flush()
            {
                if (token.Length > 0)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                }
            }
        }

        /// <summary>The instructions up to the closing brace: numbers, operator names and nested blocks.</summary>
        private static List<object>? Block(List<string> tokens, ref int at, int depth)
        {
            if (depth > 32)
            {
                return null;
            }

            var block = new List<object>();
            while (at < tokens.Count)
            {
                var token = tokens[at++];
                switch (token)
                {
                    case "}":
                        return block;
                    case "{":
                        if (Block(tokens, ref at, depth + 1) is not { } inner)
                        {
                            return null;
                        }

                        block.Add(inner);
                        break;
                    default:
                        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        {
                            block.Add(number);
                        }
                        else
                        {
                            block.Add(token);
                        }

                        break;
                }
            }

            return null;
        }

        private static void Run(List<object> program, List<Value> stack, ref int steps)
        {
            for (var i = 0; i < program.Count; i++)
            {
                if (++steps > MaxSteps || stack.Count > MaxStack)
                {
                    throw new FormatException("The calculator runs too long.");
                }

                switch (program[i])
                {
                    case double number:
                        stack.Add(new Value(number, false));
                        continue;
                    case List<object> block when i + 1 < program.Count && program[i + 1] is "if":
                        if (Pop(stack).IsTrue)
                        {
                            Run(block, stack, ref steps);
                        }

                        i++;
                        continue;
                    case List<object> then when i + 2 < program.Count && program[i + 1] is List<object> otherwise && program[i + 2] is "ifelse":
                        Run(Pop(stack).IsTrue ? then : otherwise, stack, ref steps);
                        i += 2;
                        continue;
                    case string name:
                        Operator(name, stack);
                        continue;
                    default:
                        throw new FormatException("A block stands where no if or ifelse uses it.");
                }
            }
        }

        private static void Operator(string name, List<Value> stack)
        {
            switch (name)
            {
                case "true":
                    stack.Add(new Value(1, true));
                    return;
                case "false":
                    stack.Add(new Value(0, true));
                    return;
                case "dup":
                    stack.Add(Peek(stack, 0));
                    return;
                case "pop":
                    Pop(stack);
                    return;
                case "exch":
                    var (b, a) = (Pop(stack), Pop(stack));
                    stack.Add(b);
                    stack.Add(a);
                    return;
                case "copy":
                    var n = (int)Pop(stack).Number;
                    if (n < 0 || n > stack.Count)
                    {
                        throw new FormatException("copy out of range");
                    }

                    stack.AddRange(stack.Skip(stack.Count - n).ToList());
                    return;
                case "index":
                    stack.Add(Peek(stack, (int)Pop(stack).Number));
                    return;
                case "roll":
                    var j = (int)Pop(stack).Number;
                    var count = (int)Pop(stack).Number;
                    if (count < 0 || count > stack.Count)
                    {
                        throw new FormatException("roll out of range");
                    }

                    if (count > 0)
                    {
                        var items = stack.Skip(stack.Count - count).ToList();
                        stack.RemoveRange(stack.Count - count, count);
                        var shift = ((j % count) + count) % count;
                        stack.AddRange(items.Skip(count - shift).Concat(items.Take(count - shift)));
                    }

                    return;
            }

            if (name is "not")
            {
                var v = Pop(stack);
                stack.Add(v.IsBoolean ? new Value(v.IsTrue ? 0 : 1, true) : new Value(~(long)v.Number, false));
                return;
            }

            if (name is "and" or "or" or "xor")
            {
                var (y, x) = (Pop(stack), Pop(stack));
                if (x.IsBoolean && y.IsBoolean)
                {
                    var result = name switch { "and" => x.IsTrue && y.IsTrue, "or" => x.IsTrue || y.IsTrue, _ => x.IsTrue ^ y.IsTrue };
                    stack.Add(new Value(result ? 1 : 0, true));
                }
                else
                {
                    var (l, r) = ((long)x.Number, (long)y.Number);
                    stack.Add(new Value(name switch { "and" => l & r, "or" => l | r, _ => l ^ r }, false));
                }

                return;
            }

            if (name is "eq" or "ne" or "gt" or "ge" or "lt" or "le")
            {
                var (y, x) = (Pop(stack).Number, Pop(stack).Number);
                var result = name switch { "eq" => x == y, "ne" => x != y, "gt" => x > y, "ge" => x >= y, "lt" => x < y, _ => x <= y };
                stack.Add(new Value(result ? 1 : 0, true));
                return;
            }

            if (name is "add" or "sub" or "mul" or "div" or "idiv" or "mod" or "exp" or "atan" or "bitshift")
            {
                var (y, x) = (Pop(stack).Number, Pop(stack).Number);
                var result = name switch
                {
                    "add" => x + y,
                    "sub" => x - y,
                    "mul" => x * y,
                    "div" => y == 0 ? 0 : x / y,
                    "idiv" => y == 0 ? 0 : Math.Truncate(x / y),
                    "mod" => y == 0 ? 0 : x % y,
                    "exp" => Math.Pow(x, y),
                    "atan" => (Math.Atan2(x, y) * 180 / Math.PI + 360) % 360,
                    _ => y >= 0 ? (long)x << (int)y : (long)x >> (int)-y,
                };
                stack.Add(new Value(result, false));
                return;
            }

            var operand = Pop(stack).Number;
            var value = name switch
            {
                "abs" => Math.Abs(operand),
                "neg" => -operand,
                "ceiling" => Math.Ceiling(operand),
                "floor" => Math.Floor(operand),
                "round" => Math.Floor(operand + 0.5),
                "truncate" or "cvi" => Math.Truncate(operand),
                "cvr" => operand,
                "sqrt" => Math.Sqrt(Math.Max(0, operand)),
                "sin" => Math.Sin(operand * Math.PI / 180),
                "cos" => Math.Cos(operand * Math.PI / 180),
                "ln" => Math.Log(operand),
                "log" => Math.Log10(operand),
                _ => throw new FormatException("Unknown calculator operator."),
            };
            stack.Add(new Value(value, false));
        }

        private static Value Pop(List<Value> stack)
        {
            if (stack.Count == 0)
            {
                throw new FormatException("The calculator stack is empty.");
            }

            var value = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return value;
        }

        private static Value Peek(List<Value> stack, int depth)
            => depth >= 0 && depth < stack.Count ? stack[stack.Count - 1 - depth] : throw new FormatException("index out of range");

        private readonly record struct Value(double Number, bool IsBoolean)
        {
            public bool IsTrue => Number != 0;
        }
    }
}
