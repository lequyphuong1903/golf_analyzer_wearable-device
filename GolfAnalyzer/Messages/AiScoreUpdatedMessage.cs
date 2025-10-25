using CommunityToolkit.Mvvm.Messaging.Messages;

namespace GolfAnalyzer.Messages;

public sealed class AiScoreUpdatedMessage : ValueChangedMessage<double>
{
    public AiScoreUpdatedMessage(double value) : base(value) { }
}