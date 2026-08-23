namespace Hyperwyc;

/// <summary>
/// Per-request options a caller can attach to an <see cref="HttpRequestMessage"/> to
/// influence how Hyperwyc treats it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HttpRequestOptions"/> is local to the message and is never serialised or
/// transmitted, so nothing here reaches the server. That matters: it is what lets Hyperwyc
/// accept per-request instruction without violating
/// <see href="https://github.com/mattgoldman/hyperwyc/blob/main/docs/decisions/0001-idempotency-is-not-hyperwycs-remit.md">ADR 0001</see>,
/// which holds that Hyperwyc adds nothing to outbound requests.
/// </para>
/// </remarks>
public static class HyperwycRequestOptions
{
    /// <summary>
    /// A value identifying this request to the application, echoed back on every
    /// <see cref="Models.SyncEvent"/> concerning it so a deferred outcome can be matched to
    /// the record that produced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Optional. Hyperwyc generates one if you do not set it and returns it on the
    /// <c>202</c> as <c>X-Hyperwyc-Correlation-Id</c>. Setting it is worthwhile when the
    /// application already has an identifier for the thing being written — an order id, a
    /// draft's primary key — because then no mapping table is needed:
    /// </para>
    /// <code>
    /// var request = new HttpRequestMessage(HttpMethod.Post, "/sales")
    /// {
    ///     Content = JsonContent.Create(sale)
    /// };
    /// request.Options.Set(HyperwycRequestOptions.CorrelationId, sale.Id.ToString());
    /// </code>
    /// <para>
    /// The JSON convenience methods (<c>PostAsJsonAsync</c> and friends) do not expose
    /// <see cref="HttpRequestOptions"/>, so supplying your own means building the message.
    /// That is the only cost, and only for callers who want their own value.
    /// </para>
    /// <para>
    /// <b>Hyperwyc does not require this to be unique and never deduplicates on it.</b> It is
    /// the application's key, carrying the application's meaning — several writes deliberately
    /// sharing one value is a legitimate thing to want. It is not an idempotency key; see
    /// ADR 0001 for why Hyperwyc has no opinion on duplicate suppression.
    /// </para>
    /// </remarks>
    public static readonly HttpRequestOptionsKey<string> CorrelationId =
        new("Hyperwyc.CorrelationId");
}
