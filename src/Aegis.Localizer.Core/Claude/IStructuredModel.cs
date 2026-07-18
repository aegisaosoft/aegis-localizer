/*
 * Copyright (c) 2025-2026 Aegis AO Soft LLC and Alexander Orlov.
 * 34 Middletown Ave, Atlantic Highlands, NJ 07716
 *
 * THIS SOFTWARE IS THE CONFIDENTIAL AND PROPRIETARY INFORMATION OF
 * Aegis AO Soft LLC and Alexander Orlov.
 *
 * This code may be used, reproduced, modified, or distributed ONLY with the
 * prior written permission of Aegis AO Soft LLC / Alexander Orlov.
 *
 * Author: Alexander Orlov
 * Aegis AO Soft LLC
 */

namespace Aegis.Localizer.Claude;

/// <summary>
/// The single seam between the pipeline and the model. Today it is the user's own Anthropic key
/// talking straight to the API; a hosted proxy or an offline stub slots in here without the rest
/// of the tool noticing.
/// </summary>
public interface IStructuredModel
{
    /// <summary>
    /// Sends one prompt and forces a reply through <paramref name="toolName"/>, then deserializes
    /// that tool's arguments into <typeparamref name="T"/>. Schema validation happens at the model
    /// boundary, so callers never parse free text.
    /// </summary>
    Task<T> ExtractAsync<T>(
        string system,
        string userText,
        string toolName,
        string toolDescription,
        object inputSchema,
        CancellationToken ct);

    long InputTokens { get; }

    long OutputTokens { get; }
}
