using System.Text;

namespace SMSO.Net;

public static class PlayerNameValidator
{
    public const int MinLength = 3;
    public const int MaxLength = 16;
    public const int MaxUtf8Bytes = 15;

    public const string InvalidNameHint =
        "Username must be 3-16 characters and may use letters, digits, spaces, underscores, and punctuation.";

    public static bool IsAllowedChar(char c)
    {
        if (char.IsControl(c) || char.IsSurrogate(c))
            return false;

        return char.IsLetterOrDigit(c) || c == '_' || c == ' ' || char.IsPunctuation(c);
    }

    public static bool TryValidate(string? name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = InvalidNameHint;
            return false;
        }

        if (name.Length < MinLength || name.Length > MaxLength)
        {
            error = InvalidNameHint;
            return false;
        }

        foreach (var c in name)
        {
            if (!IsAllowedChar(c))
            {
                error = InvalidNameHint;
                return false;
            }
        }

        if (Encoding.UTF8.GetByteCount(name) > MaxUtf8Bytes)
        {
            error = "Username is too long (max 15 UTF-8 bytes). Try a shorter name.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static void ValidateOrThrow(string name)
    {
        if (!TryValidate(name, out var error))
            throw new InvalidOperationException(error);
    }
}
