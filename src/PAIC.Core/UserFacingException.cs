namespace PAIC.Core;

/// <summary>Base class for errors whose message is safe and useful to show directly to the user.</summary>
public class UserFacingException : Exception
{
    public UserFacingException(string message) : base(message) { }
    public UserFacingException(string message, Exception inner) : base(message, inner) { }
}
