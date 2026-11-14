using YtAuto.Core.Domain;

namespace YtAuto.Core.Pipeline;

/// <summary>Pipeline xatosi: toifa retry/NeedsAttention qarorini belgilaydi, Code UI va loglar uchun barqaror.</summary>
public class PipelineException : Exception
{
    public ErrorCategory Category { get; }
    public string Code { get; }

    public PipelineException(ErrorCategory category, string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Category = category;
        Code = code;
    }

    public static PipelineException Config(string code, string message) => new(ErrorCategory.Configuration, code, message);
    public static PipelineException Transient(string code, string message, Exception? inner = null) => new(ErrorCategory.Transient, code, message, inner);
    public static PipelineException Quality(string code, string message) => new(ErrorCategory.Quality, code, message);
    public static PipelineException Ambiguous(string code, string message) => new(ErrorCategory.Ambiguous, code, message);
}

/// <summary>Montaj/render natijasidagi, Claude tuzata oladigan kamchiliklar (cheklangan tuzatish sikli).</summary>
public sealed class EditorFixableException : PipelineException
{
    public Qa.QaReport Report { get; }
    public EditorFixableException(string code, string message, Qa.QaReport report)
        : base(ErrorCategory.Quality, code, message) => Report = report;
}

/// <summary>Foydalanuvchi qarori kerak (masalan, public vaqti o'tib ketgan): NeedsAttention, retry hisoblanmaydi.</summary>
public sealed class AttentionRequiredException : PipelineException
{
    public AttentionRequiredException(string code, string message) : base(ErrorCategory.Configuration, code, message) { }
}

public static class Retry
{
    /// <summary>Faqat Transient xatolar uchun cheklangan eksponensial backoff.</summary>
    public static async Task<T> TransientAsync<T>(Func<int, Task<T>> action, int maxAttempts, Action<int, Exception>? onRetry,
        CancellationToken ct, TimeSpan? baseDelay = null)
    {
        var delay = baseDelay ?? TimeSpan.FromSeconds(5);
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await action(attempt).ConfigureAwait(false); }
            catch (PipelineException ex) when (ex.Category == ErrorCategory.Transient && attempt < maxAttempts)
            {
                onRetry?.Invoke(attempt, ex);
                var wait = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * Math.Pow(2, attempt - 1) * (0.85 + Random.Shared.NextDouble() * 0.3));
                if (wait > TimeSpan.FromMinutes(5)) wait = TimeSpan.FromMinutes(5);
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
    }
}
