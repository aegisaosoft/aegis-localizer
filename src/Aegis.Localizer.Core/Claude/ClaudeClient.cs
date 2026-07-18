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

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aegis.Localizer.Claude;

public sealed class ClaudeException(string message) : Exception(message);

public sealed class ClaudeOptions
{
    public required string ApiKey { get; init; }
    public string Model { get; init; } = "claude-sonnet-5";
    public int MaxTokens { get; init; } = 8000;
}

/// <summary>
/// Minimal Anthropic Messages client. Only what this tool needs: one structured call that forces
/// the model to answer through a tool, so the result is schema-validated JSON instead of prose.
/// </summary>
public sealed class ClaudeClient : IStructuredModel, IDisposable
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly ClaudeOptions _opts;

    public ClaudeClient(ClaudeOptions opts, HttpClient? http = null)
    {
        _opts = opts;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    private long _inputTokens;
    private long _outputTokens;

    /// <summary>Total input/output tokens billed across this client's lifetime.</summary>
    public long InputTokens => Interlocked.Read(ref _inputTokens);

    public long OutputTokens => Interlocked.Read(ref _outputTokens);

    /// <summary>
    /// Sends one prompt and forces the model to reply by calling <paramref name="toolName"/>,
    /// then deserializes that tool's input into <typeparamref name="T"/>.
    /// </summary>
    public async Task<T> ExtractAsync<T>(
        string system,
        string userText,
        string toolName,
        string toolDescription,
        object inputSchema,
        CancellationToken ct)
    {
        var body = new
        {
            model = _opts.Model,
            max_tokens = _opts.MaxTokens,
            system,
            tools = new object[]
            {
                new { name = toolName, description = toolDescription, input_schema = inputSchema }
            },
            tool_choice = new { type = "tool", name = toolName },
            messages = new object[]
            {
                new { role = "user", content = userText }
            }
        };

        using var doc = await SendAsync(body, ct);

        foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
        {
            if (block.GetProperty("type").GetString() != "tool_use") continue;
            if (block.GetProperty("name").GetString() != toolName) continue;

            var input = block.GetProperty("input").GetRawText();
            var result = JsonSerializer.Deserialize<T>(input, JsonOpts);
            if (result is null) throw new ClaudeException($"Tool '{toolName}' returned an empty payload.");
            return result;
        }

        throw new ClaudeException($"Model did not call tool '{toolName}'.");
    }

    private async Task<JsonDocument> SendAsync(object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);

        // Retries cover the transient 429/5xx/overloaded cases; anything else fails fast.
        var delay = TimeSpan.FromSeconds(2);
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.TryAddWithoutValidation("x-api-key", _opts.ApiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            string text;
            try
            {
                resp = await _http.SendAsync(req, ct);
                text = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException) when (attempt < 5)
            {
                await Task.Delay(delay, ct);
                delay *= 2;
                continue;
            }

            using (resp)
            {
                if (resp.IsSuccessStatusCode)
                {
                    var doc = JsonDocument.Parse(text);
                    Meter(doc);
                    return doc;
                }

                var retryable = resp.StatusCode is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.InternalServerError
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout
                    or (HttpStatusCode)529;

                if (!retryable || attempt >= 5)
                    throw new ClaudeException($"Anthropic API {(int)resp.StatusCode}: {Truncate(text, 1200)}");
            }

            await Task.Delay(delay, ct);
            delay *= 2;
        }
    }

    private void Meter(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("usage", out var usage)) return;

        // Batches run in parallel, so a plain += would lose updates and under-report a number the
        // user reads as their bill.
        if (usage.TryGetProperty("input_tokens", out var i)) Interlocked.Add(ref _inputTokens, i.GetInt64());
        if (usage.TryGetProperty("output_tokens", out var o)) Interlocked.Add(ref _outputTokens, o.GetInt64());
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public void Dispose() => _http.Dispose();
}
