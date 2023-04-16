using Spectre.Console;
using System.CommandLine;
using System.Text.RegularExpressions;
using System.Threading.Tasks.Dataflow;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        RootCommand rootCommand = new();
        Argument<string> regexPatternArgument = new Argument<string>("regex");
        Argument<string> filePatternArgument = new Argument<string>("file pattern", () => "*");
        rootCommand.AddArgument(regexPatternArgument);
        rootCommand.AddArgument(filePatternArgument);
        rootCommand.SetHandler(async (regexPattern, filePattern) =>
        {
            Program program = new(regexPattern, filePattern);
            await program.Run();
        },
        regexPatternArgument,
        filePatternArgument);

        return await rootCommand.InvokeAsync(args);
    }

    private readonly string _regexPattern;
    private readonly string _filePattern;
    private readonly Regex _regex;

    private Program(string regexPattern, string filePattern)
    {
        _regex = new Regex(regexPattern);
        _regexPattern = regexPattern;
        _filePattern = filePattern;
    }

    private async Task Run()
    {
        TransformManyBlock<string, FileReadResult> fileToLineTransformer = new(FileReadTransformFunction);
        TransformManyBlock<FileReadResult, MatchResult> lineToMatchTransformer = new(LineMatchTransformFunction);
        ActionBlock<MatchResult> matchHandler = new(PrintMatch);

        fileToLineTransformer.LinkTo(lineToMatchTransformer, new() { PropagateCompletion = true });
        lineToMatchTransformer.LinkTo(matchHandler, new() { PropagateCompletion = true });

        string currentDirectory = Environment.CurrentDirectory;
        EnumerationOptions enumerationOptions = new()
        {
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.PlatformDefault,
            MatchType = MatchType.Simple,
            RecurseSubdirectories = true
        };

        foreach (string file in Directory.EnumerateFiles(currentDirectory, _filePattern, enumerationOptions))
        {
            fileToLineTransformer.Post(file);
        }

        fileToLineTransformer.Complete();

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
            AnsiConsole.Markup($"[grey]{relativePath}: [/][red]{errorMatchResult.Message}[/]");
            AnsiConsole.WriteLine();
        }
        else if (matchResult is LineMatchResult lineMatchResult)
        {
            AnsiConsole.Markup($"[grey]{relativePath}, {lineMatchResult.LineNumber}: [/][yellow]{lineMatchResult.Line}[/]");
            AnsiConsole.WriteLine();
        }
    }
}

abstract record class FileReadResult(string FilePath);
record class ErrorReadResult(string FilePath, string Message) : FileReadResult(FilePath);
record class LineReadResult(string FilePath, int LineNumber, string Line) : FileReadResult(FilePath);

abstract record class MatchResult(string FilePath);
record class ErrorMatchResult(string FilePath, string Message) : MatchResult(FilePath);
record class LineMatchResult(string FilePath, int LineNumber, int Column, int Length, string Line) : MatchResult(FilePath);