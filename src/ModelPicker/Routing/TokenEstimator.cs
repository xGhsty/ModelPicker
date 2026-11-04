using ModelPicker.Models;

namespace ModelPicker.Routing;

public static class TokenEstimator
{
    // ~3.5 characters per token is a reasonable average for mixed English/Polish text and code.
    public static int Estimate(int characters) => (int)Math.Ceiling(characters / 3.5);

    public static int Estimate(IEnumerable<ChatMessage> messages) => Estimate(messages.Sum(m => m.Content.Length));
}
