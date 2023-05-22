using Spectre.Console;
using System.CommandLine;
using System.Text.RegularExpressions;
using System.Threading.Tasks.Dataflow;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        Argument<string> regexPatternArgument = new(
            name: "regex",
            description: "The text to be searched for.");
        Argument<string> filePatternArgument = new Argument<string>(
            name: "file pattern",
            getDefaultValue: () => "*",
            description: "The files to search for.");

        Option<bool> recurse = new(
            aliases: new[] { "--recurse", "-r" },
            getDefaultValue: () => true,
            description: "Whether or not to recurse into subdirectories");

        Option<bool> insensitive = new(
            aliases: new[] { "--insensitive", "-i" },
            getDefaultValue: () => true,
            description: "Whether to consider case while matching the pattern");

        RootCommand rootCommand = new(description: "A utility for searching for text.");
        rootCommand.AddArgument(regexPatternArgument);
        rootCommand.AddArgument(filePatternArgument);
        rootCommand.AddOption(recurse);
        rootCommand.AddOption(insensitive);

        rootCommand.SetHandler(async (regexPattern, filePattern, recurse, insensitive) =>
        {
            Program program = new(regexPattern, filePattern, recurse, insensitive);
            await program.Run();
        },
        regexPatternArgument,
        filePatternArgument,
        recurse,
        insensitive);

        return await rootCommand.InvokeAsync(args);
    }

    private readonly string _regexPattern;
    private readonly string _filePattern;
    private readonly bool _recurse;
    private readonly Regex _regex;

    private Program(string regexPattern, string filePattern, bool recurse, bool insensitive)
    {
        RegexOptions options = insensitive ? RegexOptions.IgnoreCase : RegexOptions.None;
        _regex = new Regex(regexPattern, options);
        _regexPattern = regexPattern;
        _filePattern = filePattern;
        _recurse = recurse;
    }

    private async Task Run()
    {
        // Create a dataflow block to read individual lines from matching files.
        TransformManyBlock<string, FileReadResult> fileToLineTransformer = new(FileReadTransformFunction);
        
        // Create a dataflow block to match lines against the desired text.
        TransformManyBlock<FileReadResult, MatchResult> lineToMatchTransformer = new(LineMatchTransformFunction);
        
        // Create a dataflow block to output the matches.
        ActionBlock<MatchResult> matchHandler = new(PrintMatch);

        fileToLineTransformer.LinkTo(lineToMatchTransformer, new() { PropagateCompletion = true });
        lineToMatchTransformer.LinkTo(matchHandler, new() { PropagateCompletion = true });

        string currentDirectory = Environment.CurrentDirectory;
        EnumerationOptions enumerationOptions = new()
        {
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.PlatformDefault,
            MatchType = MatchType.Simple,
            RecurseSubdirectories = _recurse
        };

        // Find all matching files...
        foreach (string file in Directory.EnumerateFiles(currentDirectory, _filePattern, enumerationOptions))
        {
            // ... and push them to the first dataflow block.
            fileToLineTransformer.Post(file);
        }

        // Mark the first block as complete...
        fileToLineTransformer.Complete();

        // ... and wait for the last one to finish.
        await matchHandler.Completion;
    }

    private async IAsyncEnumerable<FileReadResult> FileReadTransformFunction(string filePath)
    {
        int lineNumber = 1;
        await foreach (string line in File.ReadLinesAsync(filePath))
        {
            yield return new LineReadResult(filePath, lineNumber, line);
            lineNumber++;
        }
    }

    private IEnumerable<MatchResult> LineMatchTransformFunction(FileReadResult readResult)
    {
        if (readResult is ErrorReadResult errorReadResult)
        {
            yield return new ErrorMatchResult(errorReadResult.FilePath, errorReadResult.Message);
        }
        else if (readResult is LineReadResult lineReadResult)
        {
            if (_regex.Match(lineReadResult.Line) is { Success: true } match)
            {
                yield return new LineMatchResult(
                    lineReadResult.FilePath,
                    lineReadResult.LineNumber,
                    match.Index,
                    match.Length,
                    lineReadResult.Line);
            }
        }
    }

    private void PrintMatch(MatchResult matchResult)
    {
        string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, matchResult.FilePath);

        if (matchResult is ErrorMatchResult errorMatchResult)
        {
            AnsiConsole.MarkupInterpolated($"[grey]{relativePath}: [/][red]{errorMatchResult.Message}[/]");
            AnsiConsole.WriteLine();
        }
        else if (matchResult is LineMatchResult lineMatchResult)
        {
            AnsiConsole.MarkupInterpolated($"[grey]{relativePath}, {lineMatchResult.LineNumber}: [/][yellow]{lineMatchResult.Line}[/]");
            AnsiConsole.WriteLine();
        }
    }
}

/// <summary>
///   Represents the result of attempting to read lines from a file.
/// </summary>
abstract record class FileReadResult(string FilePath);

/// <summary>
///   A <see cref="FileReadResult"/> for when an error occurred while trying to read a file.
/// </summary>
record class ErrorReadResult(string FilePath, string Message) : FileReadResult(FilePath);

/// <summary>
///   Represents a single line successfully read from a file.
/// </summary>
record class LineReadResult(string FilePath, int LineNumber, string Line) : FileReadResult(FilePath);

/// <summary>
///   Represents the result of attempting to match a regex against a <see cref="FileReadResult"/>.
/// </summary>
abstract record class MatchResult(string FilePath);

/// <summary>
///   A <see cref="MatchResult"/> representing an <see cref="ErrorReadResult"/>.
/// </summary>
record class ErrorMatchResult(string FilePath, string Message) : MatchResult(FilePath);

/// <summary>
///   Represents a successful match.
/// </summary>
record class LineMatchResult(string FilePath, int LineNumber, int Column, int Length, string Line) : MatchResult(FilePath);
