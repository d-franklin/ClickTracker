namespace ClickTracker.Links;

/// <summary>Thrown when a link definition does not satisfy the ID or URL rules.</summary>
public sealed class InvalidLinkException : Exception
{
    public InvalidLinkException(string message) : base(message)
    {
    }
}
