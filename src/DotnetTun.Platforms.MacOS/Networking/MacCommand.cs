namespace DotnetTun.Platforms.MacOS.Networking;

public sealed class MacCommand : IEquatable<MacCommand>
{
    private readonly string[] _arguments;

    public MacCommand(string Executable, IReadOnlyList<string> Arguments, bool IgnoreFailure = false)
    {
        this.Executable = Executable;
        _arguments = Arguments?.ToArray() ?? throw new ArgumentNullException(nameof(Arguments));
        this.Arguments = Array.AsReadOnly(_arguments);
        this.IgnoreFailure = IgnoreFailure;
    }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public bool IgnoreFailure { get; }

    public override string ToString()
        => _arguments.Length == 0 ? Executable : $"{Executable} {string.Join(' ', _arguments)}";

    public bool Equals(MacCommand? other)
        => other is not null
            && Executable == other.Executable
            && IgnoreFailure == other.IgnoreFailure
            && _arguments.SequenceEqual(other._arguments);

    public override bool Equals(object? obj)
        => Equals(obj as MacCommand);

    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        hashCode.Add(Executable);
        hashCode.Add(IgnoreFailure);
        foreach (string argument in _arguments)
        {
            hashCode.Add(argument);
        }

        return hashCode.ToHashCode();
    }
}
