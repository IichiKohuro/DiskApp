#:package Microsoft.CodeAnalysis.CSharp@4.14.0
#:property ManagePackageVersionsCentrally=false
#:property TreatWarningsAsErrors=false
#:property EnforceCodeStyleInBuild=false
#:property GenerateDocumentationFile=false
#:property RunAnalyzersDuringBuild=false
#:property NoWarn=$(NoWarn);CS1591;NU1608

// Локализация строк DiskApp (PLAN.md, этап 9.2).
//
//   dotnet run scripts/l10n/L10n.cs -- extract <папка проекта> <candidates.tsv>
//       Находит строки с кириллицей: литералы и интерполированные строки C# (кроме журналов, атрибутов, констант,
//       меток case и значений параметров по умолчанию) и значения атрибутов AXAML. Пишет TSV: id, файл, строка, вид,
//       текст для ресурса (дыры интерполяции — {0}, {1}…), выражения дыр, контекст.
//
//   dotnet run scripts/l10n/L10n.cs -- apply <папка проекта> <candidates.tsv> <translations.tsv> <resx без .resx> <пространство имён L>
//       translations.tsv: id<TAB>английский текст | id<TAB>- (пропустить). Ключ строится из английского текста
//       («Префикс.ПервыеСлова»), одинаковый русский текст с тем же префиксом получает один ключ. Дополняет
//       <resx>.resx (русский) и <resx>.en.resx (английский) и заменяет строки в исходниках: C# — L.T("Key") или
//       L.F("Key", дыры…), AXAML — {loc:Str Key}.
//
//   dotnet run scripts/l10n/L10n.cs -- add <resx без .resx> <keys.tsv>
//       keys.tsv: ключ<TAB>русский<TAB>английский — строки, добавленные вручную.
//
//   dotnet run scripts/l10n/L10n.cs -- check <resx без .resx>
//       Проверяет, что у каждого ключа есть английский текст с теми же подстановками.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var command = args.Length > 0 ? args[0] : string.Empty;
return command switch
{
    "extract" when args.Length == 3 => Extract(args[1], args[2]),
    "apply" when args.Length == 6 => Apply(args[1], args[2], args[3], args[4], args[5]),
    "add" when args.Length == 3 => Add(args[1], args[2]),
    "check" when args.Length == 2 => Check(args[1]),
    "spaces" when args.Length == 2 => Spaces(args[1]),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Использование: extract | apply | add | check — подробности в начале файла.");
    return 2;
}

static int Extract(string projectDir, string output)
{
    var candidates = new List<Candidate>();
    foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories).Where(IsSource).Order(StringComparer.Ordinal))
    {
        candidates.AddRange(CSharpCandidates(file));
    }

    foreach (var file in Directory.EnumerateFiles(projectDir, "*.axaml", SearchOption.AllDirectories).Where(IsSource).Order(StringComparer.Ordinal))
    {
        candidates.AddRange(AxamlCandidates(file));
    }

    var lines = candidates.Select((candidate, index) => string.Join('\t',
        $"S{index + 1:D4}", Rel(projectDir, candidate.File), candidate.Line.ToString(CultureInfo.InvariantCulture), candidate.Kind,
        Escape(candidate.Text), Escape(string.Join(" ␟ ", candidate.Holes)), Escape(candidate.Context)));
    File.WriteAllLines(output, ["id\tfile\tline\tkind\ttext\tholes\tcontext", .. lines], new UTF8Encoding(false));
    Console.WriteLine($"{candidates.Count} строк → {output}");
    return 0;
}

static bool IsSource(string path)
    => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

static bool HasCyrillic(string text) => text.Any(static c => c is >= 'А' and <= 'я' or 'Ё' or 'ё');

static IEnumerable<Candidate> CSharpCandidates(string file)
{
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
    var root = tree.GetRoot();
    foreach (var node in root.DescendantNodes())
    {
        if (node is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
            && literal.Parent is not InterpolationSyntax
            && HasCyrillic(literal.Token.ValueText)
            && !IsExcluded(literal))
        {
            yield return new Candidate(file, Line(literal), "cs", literal.Token.ValueText, [], Context(literal), literal.Span);
        }
        else if (node is InterpolatedStringExpressionSyntax interpolated && !IsExcluded(interpolated))
        {
            var text = new StringBuilder();
            var holes = new List<string>();
            foreach (var content in interpolated.Contents)
            {
                if (content is InterpolatedStringTextSyntax part)
                {
                    text.Append(part.TextToken.ValueText.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));
                }
                else if (content is InterpolationSyntax hole)
                {
                    text.Append('{').Append(holes.Count.ToString(CultureInfo.InvariantCulture));
                    if (hole.AlignmentClause is { } alignment)
                    {
                        text.Append(',').Append(alignment.Value.ToString());
                    }

                    if (hole.FormatClause is { } format)
                    {
                        text.Append(':').Append(format.FormatStringToken.ValueText);
                    }

                    text.Append('}');
                    holes.Add(hole.Expression.ToString());
                }
            }

            if (HasCyrillic(text.ToString()))
            {
                // Русская строка внутри дыры заменяется отдельно; внешнюю строку тогда переводят вручную.
                var nested = interpolated.Contents.OfType<InterpolationSyntax>().Any(static hole => hole.DescendantNodes().OfType<LiteralExpressionSyntax>().Any(static inner => HasCyrillic(inner.Token.ValueText)));
                yield return new Candidate(file, Line(interpolated), nested ? "csi-manual" : "csi", text.ToString(), holes, Context(interpolated), interpolated.Span);
            }
        }
    }
}

static bool IsExcluded(SyntaxNode node)
{
    foreach (var ancestor in node.Ancestors())
    {
        switch (ancestor)
        {
            case AttributeSyntax:
            case CaseSwitchLabelSyntax:
            case ConstantPatternSyntax:
            case ParameterSyntax:
                return true;
            case FieldDeclarationSyntax field when field.Modifiers.Any(SyntaxKind.ConstKeyword):
            case LocalDeclarationStatementSyntax local when local.IsConst:
                return true;
            case InvocationExpressionSyntax invocation when IsLogging(invocation):
                return true;
            case MemberDeclarationSyntax:
                return false;
        }
    }

    return false;
}

static bool IsLogging(InvocationExpressionSyntax invocation)
    => invocation.Expression is MemberAccessExpressionSyntax access
        && access.Name.Identifier.ValueText is "Verbose" or "Debug" or "Information" or "Warning" or "Error" or "Fatal" or "Write" or "LogDebug" or "LogInformation" or "LogWarning" or "LogError"
        && access.Expression.ToString() is var receiver
        && (receiver.Contains("log", StringComparison.OrdinalIgnoreCase) || receiver.Contains("Log", StringComparison.Ordinal));

static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

static string Context(SyntaxNode node)
{
    var type = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? string.Empty;
    var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault(static m => m is not BaseTypeDeclarationSyntax) switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        ConstructorDeclarationSyntax => ".ctor",
        FieldDeclarationSyntax field => field.Declaration.Variables.First().Identifier.ValueText,
        _ => string.Empty,
    };
    var call = node.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault()?.Expression.ToString() ?? string.Empty;
    return $"{type}.{member}" + (call.Length > 0 ? $" in {call}" : string.Empty);
}

static IEnumerable<Candidate> AxamlCandidates(string file)
{
    var text = File.ReadAllText(file);
    var withoutComments = Regex.Replace(text, "<!--.*?-->", match => new string(' ', match.Length), RegexOptions.Singleline);
    foreach (Match match in Regex.Matches(withoutComments, "(?<attr>[A-Za-z][\\w.]*)=\"(?<value>[^\"{]*[А-Яа-яЁё][^\"]*)\""))
    {
        var value = match.Groups["value"].Value;
        if (value.StartsWith('{'))
        {
            continue;
        }

        var line = text[..match.Index].Count(static c => c == '\n') + 1;
        var span = new Microsoft.CodeAnalysis.Text.TextSpan(match.Groups["value"].Index, match.Groups["value"].Length);
        yield return new Candidate(file, line, "axaml", System.Net.WebUtility.HtmlDecode(value), [], match.Groups["attr"].Value, span);
    }
}

static int Apply(string projectDir, string candidatesPath, string translationsPath, string resxBase, string localizerNamespace)
{
    var candidates = ReadTsv(candidatesPath).Skip(1).ToDictionary(static row => row[0]);
    var occurrence = new Dictionary<string, int>(StringComparer.Ordinal);
    var seen = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var candidate in candidates.Values.OrderBy(static row => row[0], StringComparer.Ordinal))
    {
        var place = string.Join("|", candidate[1], candidate[2], candidate[4]);
        occurrence[candidate[0]] = seen.GetValueOrDefault(place);
        seen[place] = occurrence[candidate[0]] + 1;
    }

    var translations = ReadTsv(translationsPath).Where(static row => row.Length >= 2 && row[0].Length > 0 && !row[0].StartsWith('#')).ToDictionary(static row => row[0], static row => row[1]);
    var explicitKeys = ReadTsv(translationsPath).Where(static row => row.Length >= 3 && row[2].Length > 0).ToDictionary(static row => row[0], static row => row[2]);
    var russian = ReadResx(resxBase + ".resx");
    var english = ReadResx(resxBase + ".en.resx");
    var byText = russian.GroupBy(static pair => pair.Value).ToDictionary(static group => group.Key, static group => group.Select(static pair => pair.Key).ToList());

    var edits = new Dictionary<string, List<(int Start, int Length, string Replacement)>>(StringComparer.OrdinalIgnoreCase);
    var applied = 0;
    foreach (var (id, english_) in translations)
    {
        if (english_ == "-" || !candidates.TryGetValue(id, out var row))
        {
            continue;
        }

        if (row[3] == "csi-manual")
        {
            Console.Error.WriteLine($"{id}: строка с русскими строками внутри дыр — переведите вручную ({row[1]}:{row[2]})");
            continue;
        }

        var (file, kind, text, holes, context) = (Path.Combine(projectDir, row[1]), row[3], Unescape(row[4]), Unescape(row[5]), Unescape(row[6]));
        var englishText = english_.TrimEnd('\r');
        if (Placeholders(text) != Placeholders(englishText))
        {
            Console.Error.WriteLine($"{id}: подстановки не совпадают: «{text}» / «{englishText}»");
            continue;
        }

        var prefix = Prefix(file, kind, context);
        var key = explicitKeys.TryGetValue(id, out var given) ? given : byText.TryGetValue(text, out var keys) && keys.FirstOrDefault(existing => existing.StartsWith(prefix + ".", StringComparison.Ordinal)) is { } reuse
            ? reuse
            : NewKey(prefix, englishText, russian);
        if (russian.TryGetValue(key, out var previous) && previous != text)
        {
            Console.Error.WriteLine($"{id}: ключ {key} уже означает «{previous}», а не «{text}»");
            continue;
        }

        russian[key] = text;
        english[key] = englishText;
        if (!byText.TryGetValue(text, out var list))
        {
            byText[text] = list = [];
        }

        if (!list.Contains(key))
        {
            list.Add(key);
        }

        var replacement = kind switch
        {
            "axaml" => $"{{loc:Str {key}}}",
            "cs" => $"L.T(\"{key}\")",
            _ => $"L.F(\"{key}\", {string.Join(", ", holes.Split(" ␟ ", StringSplitOptions.None))})",
        };
        var span = Locate(file, kind, row, occurrence[id]);
        if (span is null)
        {
            Console.Error.WriteLine($"{id}: строка не найдена в {row[1]}:{row[2]}");
            continue;
        }

        if (!edits.TryGetValue(file, out var fileEdits))
        {
            edits[file] = fileEdits = [];
        }

        fileEdits.Add((span.Value.Start, span.Value.Length, replacement));
        applied++;
    }

    foreach (var (file, fileEdits) in edits)
    {
        var source = File.ReadAllText(file);
        foreach (var edit in fileEdits.OrderByDescending(static edit => edit.Start))
        {
            source = source[..edit.Start] + edit.Replacement + source[(edit.Start + edit.Length)..];
        }

        source = file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ? EnsureXmlns(source, localizerNamespace) : EnsureUsing(source, localizerNamespace);
        File.WriteAllText(file, source, new UTF8Encoding(true));
    }

    WriteResx(resxBase + ".resx", russian);
    WriteResx(resxBase + ".en.resx", english);
    Console.WriteLine($"Заменено {applied} строк в {edits.Count} файлах; ключей {russian.Count}.");
    return 0;
}

static Microsoft.CodeAnalysis.Text.TextSpan? Locate(string file, string kind, string[] row, int occurrence)
{
    // Позиции пересчитываются по свежему разбору файла: id связан с файлом, строкой и текстом.
    var line = int.Parse(row[2], CultureInfo.InvariantCulture);
    var text = Unescape(row[4]);
    var found = kind == "axaml"
        ? AxamlCandidates(file).Where(candidate => candidate.Line == line && candidate.Text == text).Skip(occurrence).FirstOrDefault()
        : CSharpCandidates(file).Where(candidate => candidate.Line == line && candidate.Text == text).Skip(occurrence).FirstOrDefault();
    return found?.Span;
}

static string Prefix(string file, string kind, string context)
{
    var name = Path.GetFileNameWithoutExtension(file);
    if (kind == "axaml")
    {
        return Trim(name);
    }

    var type = context.Split('.')[0];
    return Trim(type.Length > 0 ? type : name);

    static string Trim(string value)
    {
        foreach (var suffix in new[] { "ViewModel", "View", "Window", "Service" })
        {
            if (value.Length > suffix.Length && value.EndsWith(suffix, StringComparison.Ordinal))
            {
                return value[..^suffix.Length];
            }
        }

        return value;
    }
}

static string NewKey(string prefix, string english, Dictionary<string, string> existing)
{
    var words = Regex.Matches(Regex.Replace(english, "\\{\\d+[^}]*\\}", " "), "[A-Za-z0-9]+")
        .Select(static match => match.Value)
        .Take(6)
        .Select(static word => char.ToUpperInvariant(word[0]) + word[1..])
        .ToArray();
    var stem = prefix + "." + (words.Length > 0 ? string.Concat(words) : "Text");
    var key = stem;
    for (var index = 2; existing.ContainsKey(key); index++)
    {
        key = stem + index.ToString(CultureInfo.InvariantCulture);
    }

    return key;
}

static int Placeholders(string text) => Regex.Matches(text.Replace("{{", string.Empty, StringComparison.Ordinal), "\\{(\\d+)[^}]*\\}").Select(static match => match.Groups[1].Value).Distinct().Count();

static string EnsureUsing(string source, string ns)
{
    if (source.Contains($"using {ns};", StringComparison.Ordinal) || Regex.IsMatch(source, $"namespace {Regex.Escape(ns)}[;\\s]"))
    {
        return source;
    }

    var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    var usings = Regex.Matches(source, "^using [^\\r\\n]+;\\r?\\n", RegexOptions.Multiline);
    if (usings.Count == 0)
    {
        return $"using {ns};{newline}{newline}" + source;
    }

    // Новый using ставится после последнего using из DiskApp, иначе после последнего вообще; порядок потом поправит dotnet format.
    var anchor = usings.LastOrDefault(static match => match.Value.StartsWith("using DiskApp", StringComparison.Ordinal)) ?? usings[^1];
    var position = anchor.Index + anchor.Length;
    return source[..position] + $"using {ns};{newline}" + source[position..];
}

static string EnsureXmlns(string source, string ns)
{
    if (source.Contains("xmlns:loc=", StringComparison.Ordinal))
    {
        return source;
    }

    var match = Regex.Match(source, "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"");
    return match.Success
        ? source.Insert(match.Index + match.Length, $"{Environment.NewLine}             xmlns:loc=\"using:{ns}\"")
        : source;
}

static int Add(string resxBase, string keysPath)
{
    var russian = ReadResx(resxBase + ".resx");
    var english = ReadResx(resxBase + ".en.resx");
    foreach (var row in ReadTsv(keysPath).Where(static row => row.Length >= 3 && row[0].Length > 0 && !row[0].StartsWith('#')))
    {
        russian[row[0]] = Unescape(row[1]);
        english[row[0]] = Unescape(row[2]);
    }

    WriteResx(resxBase + ".resx", russian);
    WriteResx(resxBase + ".en.resx", english);
    Console.WriteLine($"Ключей {russian.Count}.");
    return 0;
}

static int Spaces(string resxBase)
{
    // Пробелы по краям русской строки значимы (строки склеиваются): английская получает такие же.
    var russian = ReadResx(resxBase + ".resx");
    var english = ReadResx(resxBase + ".en.resx");
    var fixedCount = 0;
    foreach (var (key, value) in russian)
    {
        if (!english.TryGetValue(key, out var translated))
        {
            continue;
        }

        var lead = value[..(value.Length - value.TrimStart().Length)];
        var trail = value[value.TrimEnd().Length..];
        var aligned = lead + translated.Trim() + trail;
        if (aligned != translated)
        {
            english[key] = aligned;
            fixedCount++;
        }
    }

    WriteResx(resxBase + ".en.resx", english);
    Console.WriteLine($"Исправлено пробелов: {fixedCount}.");
    return 0;
}

static int Check(string resxBase)
{
    var russian = ReadResx(resxBase + ".resx");
    var english = ReadResx(resxBase + ".en.resx");
    var problems = 0;
    foreach (var (key, value) in russian)
    {
        if (!english.TryGetValue(key, out var translated) || translated.Length == 0)
        {
            Console.Error.WriteLine($"нет перевода: {key}");
            problems++;
        }
        else if (Placeholders(value) != Placeholders(translated))
        {
            Console.Error.WriteLine($"подстановки не совпадают: {key}");
            problems++;
        }
    }

    Console.WriteLine($"Ключей {russian.Count}, проблем {problems}.");
    return problems == 0 ? 0 : 1;
}

static Dictionary<string, string> ReadResx(string path)
{
    if (!File.Exists(path))
    {
        return new(StringComparer.Ordinal);
    }

    return XDocument.Load(path).Root!.Elements("data")
        .ToDictionary(static data => (string)data.Attribute("name")!, static data => (string?)data.Element("value") ?? string.Empty, StringComparer.Ordinal);
}

static void WriteResx(string path, Dictionary<string, string> values)
{
    var root = new XElement("root",
        Header("resmimetype", "text/microsoft-resx"),
        Header("version", "2.0"),
        Header("reader", "System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"),
        Header("writer", "System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"));
    foreach (var (key, value) in values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
    {
        root.Add(new XElement("data", new XAttribute("name", key), new XAttribute(XNamespace.Xml + "space", "preserve"), new XElement("value", value)));
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(path);

    static XElement Header(string name, string value) => new("resheader", new XAttribute("name", name), new XElement("value", value));
}

static IEnumerable<string[]> ReadTsv(string path) => File.ReadAllLines(path).Select(static line => line.Split('\t'));

static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

static string Unescape(string value)
{
    var builder = new StringBuilder(value.Length);
    for (var i = 0; i < value.Length; i++)
    {
        if (value[i] == '\\' && i + 1 < value.Length)
        {
            i++;
            builder.Append(value[i] switch { 't' => '\t', 'r' => '\r', 'n' => '\n', _ => value[i] });
        }
        else
        {
            builder.Append(value[i]);
        }
    }

    return builder.ToString();
}

static string Rel(string root, string file) => Path.GetRelativePath(root, file);

internal sealed record Candidate(string File, int Line, string Kind, string Text, IReadOnlyList<string> Holes, string Context, Microsoft.CodeAnalysis.Text.TextSpan Span);
