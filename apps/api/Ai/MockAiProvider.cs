using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Taslim.Api.Ai;

/// <summary>
/// Development-only provider used to prove the end-to-end chat architecture.
/// This provider never calls a remote AI vendor and reports zero/test usage.
/// </summary>
public sealed class MockAiProvider(ILogger<MockAiProvider> logger) : IAiProvider
{
    public string Key => "mock";

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
        AiChatRequest request,
        AiProviderSelection selection,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var latestUserMessage = request.Messages.LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
        if (latestUserMessage.Contains("[[mock-failure]]", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Development mock provider failure sentinel invoked. ProviderKey={ProviderKey}; ModelKey={ModelKey}", selection.ProviderKey, selection.ModelKey);
            throw new MockAiProviderException();
        }

        var isLastSeed = latestUserMessage.Contains("last seed", StringComparison.OrdinalIgnoreCase)
            || latestUserMessage.Contains("young farmer", StringComparison.OrdinalIgnoreCase)
            || latestUserMessage.Contains("dry village", StringComparison.OrdinalIgnoreCase);
        var isConsistency = latestUserMessage.Contains("identify_story_inconsistencies", StringComparison.OrdinalIgnoreCase)
            || latestUserMessage.Contains("identify story inconsistencies", StringComparison.OrdinalIgnoreCase);
        var content = request.StructuredOutput?.Name == "taslim_movie_director_story"
            ? JsonSerializer.Serialize<object>(isLastSeed
                ? new
                {
                    premise = "In a dry village where drought has stopped every crop, a young farmer protects his grandfather's last seed from heat and wind until rain gives the valley a hopeful chance to grow again.",
                    logline = "When drought leaves his village with one last seed from his grandfather's vanished tree, a young farmer risks everything to protect it until the rain returns.",
                    synopsis = "A young farmer receives his grandfather's last seed in a dry village where heat and wind have killed every crop. Neighbors tell him to stop protecting what cannot survive, but he shields the seed through the hardest days. Rain finally arrives, and a green shoot turns his private hope into a future for the valley.",
                    treatment = "A 30-second cinematic arc moves from a drought-cracked village and the grandfather's gift, through the young farmer's lonely protection of the last seed against heat and wind, to a rain-soaked hopeful image of new growth. The emotional turn is earned through one clear choice and one visible consequence.",
                    replacementContent = "The young farmer shields the last seed from the next hard gust, refusing to let the valley lose its final chance.",
                    findings = isConsistency ? new[] { "bounded_consistency_review" } : Array.Empty<string>(),
                    proposedScene = new
                    {
                        sceneIdentifier = "LAST-SEED-1",
                        actNumber = 1,
                        sequenceNumber = 1,
                        slugline = "EXT. DRY VILLAGE FIELD - DAY",
                        synopsis = "The young farmer protects the last seed until rain changes the valley's future.",
                        elements = new object[]
                        {
                            new { elementType = "Action", content = "The young farmer cups dry soil around the last seed as heat and wind cross the empty field." },
                            new { elementType = "Dialogue", characterName = "YOUNG FARMER", content = "It only needs one chance." },
                            new { elementType = "Dialogue", characterName = "GRANDFATHER", content = "Then protect it." },
                            new { elementType = "Action", content = "Rain darkens the earth; a green shoot rises toward the hopeful sky." },
                        },
                    },
                }
                : new
                {
                    premise = "A guarded courier must choose whether to deliver the truth when silence would keep them safe.",
                    logline = "When a hidden message reveals imminent harm, a guarded courier must cross one dangerous night and choose who gets the truth.",
                    synopsis = "A guarded courier protects a quiet routine until a hidden message forces a public choice.",
                    treatment = "The courier moves from guarded silence to accountable action while preserving the locked guide.",
                    replacementContent = "The courier chooses the truth before the safe route closes.",
                    findings = isConsistency ? new[] { "bounded_consistency_review" } : Array.Empty<string>(),
                    proposedScene = (object?)null,
                })
            : request.StructuredOutput?.Name == "taslim_movie_synopsis_development"
            ? JsonSerializer.Serialize(isLastSeed
                ? new
                {
                    premise = "A young farmer protects his grandfather's last seed through a drought so rain can return hope to a dry village.",
                    logline = "A young farmer risks his grandfather's last seed against drought, heat, and wind until rain gives the village one hopeful beginning.",
                    treatment = "The 30-second treatment starts with a dry village and a grandfather's final gift, compresses the farmer's protection of the seed into one escalating struggle against heat and wind, and ends on rain and a single green shoot.",
                    synopsis = "In a dry village where heat and wind have stopped every crop, a young farmer receives the last seed from his grandfather. Neighbors urge him to give up, but he shields the seed through the hard days and keeps watch over the cracked soil. Rain finally arrives; the seed breaks through, turning one farmer's stubborn hope into a promise for the valley.",
                    setup = "A drought-stricken village has lost its crops, and the grandfather gives the young farmer one last seed.",
                    protagonistMotivation = "The young farmer wants to protect his grandfather's final gift and keep hope alive for the village.",
                    incitingEvent = "The grandfather places the last seed from the old valley tree in the farmer's hands.",
                    escalation = "Heat and wind threaten the seed while the village insists that it cannot survive.",
                    complications = new[] { "The farmer must protect the seed through the hottest days without water or support." },
                    climaxChoice = "The farmer keeps protecting the seed instead of surrendering to the village's certainty.",
                    resolution = "Rain arrives and a green shoot begins to grow.",
                    emotionalArc = "The farmer moves from inherited grief and isolation to shared, visible hope.",
                    canonAnchors = new[] { "young farmer", "dry village", "grandfather", "last seed", "rain" },
                    proposedElements = new[] { "the farmer's protective ritual", "the village's doubt", "the first green shoot" },
                    beatCount = 4,
                }
                : new
                {
                    premise = "A guarded courier must decide whether to deliver the truth when silence would keep them safe.",
                    logline = "When a hidden message exposes the cost of silence, a guarded courier must cross one dangerous night and choose who gets the truth.",
                    treatment = "The courier begins by protecting a fragile routine, is forced into motion by the message, loses the safety of familiar allies, and reaches a final choice where delivery matters more than escape.",
                    synopsis = "A guarded courier protects a quiet routine until an unexpected message reveals that someone will be harmed if the truth stays hidden. The courier follows a single urgent lead, but each attempt to pass the message on closes another safe route and forces a meaningful sacrifice. With no time left to remain neutral, the courier chooses to deliver the truth publicly, accepting the cost of being seen. The immediate danger recedes, yet the courier returns changed: safety is no longer measured by silence, but by the responsibility to act.",
                    setup = "The courier lives by a quiet routine built around staying unseen.",
                    protagonistMotivation = "The courier wants to protect personal safety without abandoning someone vulnerable.",
                    incitingEvent = "A message reveals an imminent harm that silence would allow.",
                    escalation = "Every attempt to pass the message safely removes another escape route.",
                    complications = new[] { "The courier's trusted route becomes unsafe, forcing a public choice." },
                    climaxChoice = "The courier delivers the truth publicly instead of disappearing with it.",
                    resolution = "The danger eases, while the courier accepts a new responsibility.",
                    emotionalArc = "The courier moves from guarded self-preservation to accountable courage.",
                    canonAnchors = new[] { "current or approved story foundation", "locked Movie Guide" },
                    proposedElements = new[] { "the hidden message", "the closing escape route", "the public delivery" },
                    beatCount = 4,
                })
            : latestUserMessage.Contains("hello taslim", StringComparison.OrdinalIgnoreCase)
            ? "Hello! Taslim Chat is connected and ready."
            : "Taslim Chat is connected and ready to help you shape that idea. This is a development response while the first real model provider is being prepared.";
        foreach (var chunk in Split(content, 18))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new AiMessageDelta(chunk);
        }

        yield return new AiMessageCompleted(new AiUsageMetadata(
            selection.ProviderKey,
            selection.ModelKey,
            InputTokens: null,
            CachedInputTokens: null,
            OutputTokens: null,
            EstimatedCost: 0m,
            ActualCost: 0m,
            LatencyMs: 0,
            FinishReason: "mock-complete",
            IsTestResponse: true));
    }

    private static IEnumerable<string> Split(string content, int chunkSize)
    {
        for (var index = 0; index < content.Length; index += chunkSize)
            yield return content[index..Math.Min(index + chunkSize, content.Length)];
    }
}

public sealed class MockAiProviderException : Exception;
