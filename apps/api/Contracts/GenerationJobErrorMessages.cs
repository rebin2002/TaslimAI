using Taslim.Api.Domain;

namespace Taslim.Api.Contracts;

/// <summary>
/// Maps internal Generation Job failure codes to bounded, user-safe messages.
/// Provider payloads, exception text, and persisted diagnostic strings never cross the public DTO boundary.
/// </summary>
public static class GenerationJobErrorMessages
{
    public static string? For(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        return code switch
        {
            GenerationJobErrorCodes.TypeNotSupported => "This job type is not available.",
            GenerationJobErrorCodes.Cancelled => "The job was cancelled.",
            GenerationJobErrorCodes.Poisoned => "The generation could not be recovered safely. Please try again.",
            GenerationJobErrorCodes.ProviderOutcomeUnconfirmed => "The provider may have accepted this request, but its result and final cost could not be confirmed. It was not restarted. Please contact support before retrying.",
            GenerationJobErrorCodes.ProviderOutcomeRetryBlocked => "This retry was blocked because the first provider attempt's result and cost remain unconfirmed.",
            GenerationJobErrorCodes.NoBillableAsset => "The generation completed without a publishable asset. Please try again.",
            GenerationJobErrorCodes.ImageStudioUnavailable => "Image generation is not available right now.",
            GenerationJobErrorCodes.ImageProviderUnavailable => "Image generation is temporarily unavailable. Please try again later.",
            GenerationJobErrorCodes.ImageRequestInvalid => "Please check the image request and try again.",
            GenerationJobErrorCodes.ImageSafetyRefusal => "This request could not be completed by the image safety system. Try a different description.",
            GenerationJobErrorCodes.ImageOutputInvalid => "The image result was invalid. Please try again.",
            GenerationJobErrorCodes.ImageOutputStorageFailed => "The image was generated but could not be saved. Please try again.",
            GenerationJobErrorCodes.ImageCancelled => "The image generation was cancelled.",
            GenerationJobErrorCodes.DocumentStudioUnavailable => "Document generation is not available right now.",
            GenerationJobErrorCodes.DocumentProviderUnavailable or GenerationJobErrorCodes.DocumentProviderConfiguration or GenerationJobErrorCodes.DocumentProviderUnsupportedRequest or GenerationJobErrorCodes.DocumentProviderRateLimited or GenerationJobErrorCodes.DocumentProviderTransientFailure => "Document generation is temporarily unavailable. Please try again later.",
            GenerationJobErrorCodes.DocumentContextTooLarge => "The selected source material is too large. Choose fewer or shorter documents.",
            GenerationJobErrorCodes.DocumentOutputInvalid => "The generated document was invalid. Please try again.",
            GenerationJobErrorCodes.DocumentStorageFailed => "The document was generated but could not be saved. Please try again.",
            GenerationJobErrorCodes.DocumentRenderFailed => "The document could not be rendered. Please try again.",
            GenerationJobErrorCodes.DocumentCancelled => "The document generation was cancelled.",
            GenerationJobErrorCodes.PresentationProviderUnavailable or GenerationJobErrorCodes.PresentationProviderConfiguration or GenerationJobErrorCodes.PresentationProviderUnsupportedRequest or GenerationJobErrorCodes.PresentationProviderRateLimited or GenerationJobErrorCodes.PresentationProviderTransientFailure => "Presentation generation is temporarily unavailable. Please try again later.",
            GenerationJobErrorCodes.PresentationContextTooLarge => "The selected source material is too large. Choose fewer or shorter documents.",
            GenerationJobErrorCodes.PresentationOutputInvalid => "The generated presentation was invalid. Please try again.",
            GenerationJobErrorCodes.PresentationStorageFailed => "The presentation was generated but could not be saved. Please try again.",
            GenerationJobErrorCodes.PresentationRenderFailed => "The presentation could not be rendered. Please try again.",
            GenerationJobErrorCodes.PresentationCancelled => "The presentation generation was cancelled.",
            GenerationJobErrorCodes.ResearchSearchUnavailable => "Web research is temporarily unavailable. Please try again later.",
            GenerationJobErrorCodes.ResearchSearchFailed => "Web research could not be completed. Please try again.",
            GenerationJobErrorCodes.ResearchProviderUnavailable => "Research generation is temporarily unavailable. Please try again later.",
            GenerationJobErrorCodes.ResearchContextTooLarge => "The selected research context is too large. Choose fewer or shorter sources.",
            GenerationJobErrorCodes.ResearchOutputInvalid => "The research report was invalid. Please try again.",
            GenerationJobErrorCodes.ResearchSourceUnavailable => "Some research sources may be missing or unavailable. Add or choose another source, then try again.",
            GenerationJobErrorCodes.ResearchSourceExtractionFailed => "One or more research sources could not be read. Choose a readable source and try again.",
            GenerationJobErrorCodes.ResearchCitationValidationFailed => "The research citations were invalid. Please try again.",
            GenerationJobErrorCodes.ResearchStorageFailed => "The research report was generated but could not be saved. Please try again.",
            GenerationJobErrorCodes.ResearchRenderFailed => "The research report could not be rendered. Please try again.",
            GenerationJobErrorCodes.ResearchCancelled => "The research generation was cancelled.",
            GenerationJobErrorCodes.SocialProviderUnavailable or GenerationJobErrorCodes.SocialProviderConfiguration or GenerationJobErrorCodes.SocialProviderUnsupportedRequest or GenerationJobErrorCodes.SocialProviderRateLimited or GenerationJobErrorCodes.SocialProviderTransientFailure => "Social content generation is temporarily unavailable. Please try again later.",
            GenerationJobErrorCodes.SocialContextTooLarge => "The selected social context is too large. Choose fewer or shorter sources.",
            GenerationJobErrorCodes.SocialOutputInvalid => "The generated social content was invalid. Please try again.",
            GenerationJobErrorCodes.SocialStorageFailed => "The social content was generated but could not be saved. Please try again.",
            GenerationJobErrorCodes.SocialCancelled => "The social content generation was cancelled.",
            GenerationJobErrorCodes.MovieProviderUnavailable => "Movie generation is not available yet because no video provider is configured. Your movie plan was saved.",
            GenerationJobErrorCodes.MovieProviderTimeout => "Movie generation took too long to finish. Your movie plan was saved.",
            GenerationJobErrorCodes.MovieProviderUnsupportedRequest => "This movie request is not supported by the configured video provider. Your movie plan was saved.",
            GenerationJobErrorCodes.MovieOutputInvalid => "The generated movie clip was invalid. Your movie plan was saved.",
            GenerationJobErrorCodes.MovieOutputStorageFailed => "The movie clip was generated but could not be saved. Your movie plan was saved.",
            GenerationJobErrorCodes.MovieCancelled => "The movie generation was cancelled.",
            GenerationJobErrorCodes.MovieGenerationFailed => "The movie could not be generated. Your movie plan was saved.",
            GenerationJobErrorCodes.MovieSoundProviderUnavailable => "Movie sound generation is temporarily unavailable. Your sound cue was saved.",
            GenerationJobErrorCodes.MovieSoundOutputInvalid => "The generated sound was invalid. Your sound cue was saved.",
            GenerationJobErrorCodes.MovieSoundOutputStorageFailed => "The sound was generated but could not be saved. Your sound cue was saved.",
            GenerationJobErrorCodes.MovieSoundCancelled => "The movie sound generation was cancelled.",
            GenerationJobErrorCodes.MovieSoundGenerationFailed => "The movie sound could not be generated. Your sound cue was saved.",
            GenerationJobErrorCodes.MusicProviderUnavailable or GenerationJobErrorCodes.MusicProviderTimeout => "Music generation is temporarily unavailable. Please try again later.",
            GenerationJobErrorCodes.MusicProviderRateLimited => "Music generation is temporarily busy. Please try again later.",
            GenerationJobErrorCodes.MusicPromptRejected => "This music request could not be completed. Try a different description.",
            GenerationJobErrorCodes.MusicProviderInvalidRequest => "Please check the music request and try again.",
            GenerationJobErrorCodes.MusicOutputInvalid => "The generated music was invalid. Please try again.",
            GenerationJobErrorCodes.MusicOutputStorageFailed => "The music was generated but could not be saved. Please try again.",
            // Both arms are exact-match and precede the MUSIC_ prefix fallback, which otherwise
            // would render any unmapped MUSIC_ code as "The music could not be generated. Please
            // try again." and invite a paid regeneration of a track that already exists. The copy
            // states what happened without asking for a retry and without claiming that automatic
            // re-delivery or a refund exists today.
            GenerationJobErrorCodes.MusicOutputRetrievalFailed => "Your music was generated, but the finished track could not be delivered. It was not generated a second time, because that would create a duplicate.",
            GenerationJobErrorCodes.MusicRetrievalRetryBlocked => "This retry was blocked because the track was already generated and only delivery failed. Please contact support to recover it.",
            // Deliberate contrast with the two arms above: here the finished track is NOT known to
            // exist, so nothing is deliverable, and the first attempt may still be running and
            // billing. The customer's retry endpoint is therefore refused as well (see
            // GenerationJobExecution.RetryAsync and the arm below): an informed retry would still be
            // a second paid submission whose sibling may already exist, and fair billing outweighs
            // the convenience of an immediate retry. The wording states the unknown outcome without
            // claiming a finished track and without inviting the customer to submit another job.
            GenerationJobErrorCodes.MusicProviderSubmissionUnconfirmed => "Music generation stopped before its outcome could be confirmed: the first attempt may already have reached the provider. It was not restarted automatically, and this job cannot be retried while that outcome is unconfirmed, so you are not billed a second time for it.",
            GenerationJobErrorCodes.MusicSubmissionRetryBlocked => "This retry was blocked because the first attempt may already have reached the provider and its outcome has not been reconciled yet. Starting another would risk a duplicate paid generation.",
            GenerationJobErrorCodes.MusicCancelled => "The music generation was cancelled.",
            // F-36: both arms distinguish a cancellation that happened AFTER the provider accepted the
            // paid generation from the ordinary one above, where nothing was submitted. Neither asserts
            // an outcome that was never observed and neither invites another submission - the accepted
            // attempt may still be running and billing, so RetryAsync refuses the retry (see
            // GenerationJobExecution.RetryAsync) and the copy points at reconciliation instead.
            GenerationJobErrorCodes.MusicCancelledUnconfirmedSubmission => "The music generation was cancelled after the provider had already accepted it, so the first attempt may still finish and may still be charged. It was not started again, and it will not be retried until that attempt has been reconciled. Please contact support if the track does not appear.",
            // Here the finished track IS established - polling saw it complete - and only the delivery
            // was lost to the cancellation, so the copy may say so without claiming a second generation.
            GenerationJobErrorCodes.MusicCancelledUndeliveredOutput => "Your music was generated, but the cancellation stopped it before the finished track could be delivered. It was not generated a second time. Please contact support to recover it.",
            GenerationJobErrorCodes.VoiceProviderUnavailable or GenerationJobErrorCodes.VoiceProviderAuthentication => "Voice generation is temporarily unavailable. Please try again later.",
            GenerationJobErrorCodes.VoiceProviderRateLimited => "Voice generation is busy right now. Please try again later.",
            GenerationJobErrorCodes.VoiceProviderTimeout => "Voice generation took too long to complete. Please try again.",
            GenerationJobErrorCodes.VoiceProviderInvalidInput => "Please check the voice request and try again.",
            GenerationJobErrorCodes.VoiceProviderUnsupportedRequest => "This voice request is not supported. Please use shorter text or different settings.",
            GenerationJobErrorCodes.VoiceProviderFailed => "Voice generation could not be completed. Please try again.",
            GenerationJobErrorCodes.VoiceLanguageUnsupported => "This language is not currently supported for voice generation.",
            GenerationJobErrorCodes.VoiceOutputInvalid => "The generated audio was invalid. Please try again.",
            GenerationJobErrorCodes.VoiceOutputStorageFailed => "The audio was generated but could not be saved. Please try again.",
            GenerationJobErrorCodes.VoiceRequestInvalid => "Please check the voice request and try again.",
            GenerationJobErrorCodes.VoiceCancelled => "The voice generation was cancelled.",
            GenerationJobErrorCodes.MovieDialogueVoiceProviderUnavailable => "Dialogue voice generation is not available yet. Your movie dialogue plan was saved.",
            GenerationJobErrorCodes.MovieDialogueVoiceRequestInvalid => "Please check the dialogue speaker, language, text, and timing.",
            GenerationJobErrorCodes.MovieDialogueVoiceOutputInvalid => "The dialogue voice result was invalid. Please try again.",
            GenerationJobErrorCodes.MovieDialogueVoiceGenerationFailed => "The dialogue voice take could not be generated. Your movie dialogue plan was saved.",
            GenerationJobErrorCodes.MovieDialogueVoiceCancelled => "The dialogue voice take was cancelled.",
            _ when code.StartsWith("IMAGE_", StringComparison.Ordinal) => "The image could not be generated. Please try again.",
            _ when code.StartsWith("DOCUMENT_", StringComparison.Ordinal) => "The document could not be generated. Please try again.",
            _ when code.StartsWith("PRESENTATION_", StringComparison.Ordinal) => "The presentation could not be generated. Please try again.",
            _ when code.StartsWith("RESEARCH_", StringComparison.Ordinal) => "The research report could not be generated. Please try again.",
            _ when code.StartsWith("SOCIAL_", StringComparison.Ordinal) => "The social content could not be generated. Please try again.",
            _ when code.StartsWith("MOVIE_", StringComparison.Ordinal) => "The movie could not be generated. Your movie plan was saved.",
            _ when code.StartsWith("MUSIC_", StringComparison.Ordinal) => "The music could not be generated. Please try again.",
            _ when code.StartsWith("VOICE_", StringComparison.Ordinal) => "The voice could not be generated. Please try again.",
            _ => "The job could not be completed.",
        };
    }
}
