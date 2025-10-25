using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using GolfAnalyzer.Messages;

namespace GolfAnalyzer.Services;

public static class HomeAnalysisHelper
{
    public static async Task<double?> RunAndBroadcastAsync(CancellationToken ct = default)
    {
        var result = await AiValidationService.RunAsync(ct);
        if (result is null) return null;

        AiScoreStore.LastBestPercent = result.BestPercent;
        WeakReferenceMessenger.Default.Send(new AiScoreUpdatedMessage(result.BestPercent));
        return result.BestPercent;
    }
}