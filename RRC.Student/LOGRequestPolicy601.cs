using RRC.Abstractions;

namespace RRC.Student;

/// <summary>
/// Адаптивная политика:
///  1) Тайм-аут подстраивается под «хвост» задержек (целевая доля тайм-аутов первой попытки ~5%).
///     Используется стохастическая аппроксимация квантиля: по каждому исходу 1-й попытки
///     (успех / тайм-аут) логарифм тайм-аута сдвигается вверх или вниз. Это корректно работает
///     и при цензурировании (при тайм-ауте реальное время ответа неизвестно).
///  2) Повторы ограничены: максимум попыток + «бюджет» повторов (token bucket),
///     чтобы при перегрузке повторы не добивали сервер.
///  3) Пауза перед повтором: экспоненциальная, пропорциональная текущему тайм-ауту,
///     с джиттером и надбавкой при высокой доле тайм-аутов (признак перегрузки).
/// </summary>
public sealed class LOGRequestPolicy601 : IRequestPolicy
{
    // ---- Параметры (универсальные, не подогнанные под один сценарий) ----
    private const double InitialTimeoutMs = 200.0;
    private const double MinTimeoutMs = 10.0;
    private const double MaxTimeoutMs = 60_000.0;

    private const double TargetTimeoutRate = 0.05;   // какую долю первых попыток допускаем тайм-аутить
    private const double MinLearningRate = 0.05;     // минимальный шаг обучения
    private const double InitialLearningRate = 0.20; // шаг в начале прогона (быстрая сходимость)

    private const int MaxAttempts = 4;               // всего попыток на запрос
    private const double RetryTokensPerRequest = 0.5; // сколько повторов «разрешено» на один запрос
    private const double MaxRetryTokens = 20.0;
    private const double PressureSmoothing = 0.05;   // сглаживание оценки доли тайм-аутов

    // ---- Состояние между запросами ----
    private double _logTimeout = Math.Log(InitialTimeoutMs);
    private long _firstAttemptsObserved;
    private double _pressure;        // сглаженная доля тайм-аутов среди первых попыток (0..1)
    private double _retryTokens = 10.0;

    public TimeSpan GetTimeout(RequestContext context, Random random)
    {
        double baseMs = Math.Exp(_logTimeout);

        // Для повторных попыток ждём чуть дольше: если сервер перегружен, у повтора больше шансов успеть.
        double factor = 1.0 + 0.5 * Math.Max(0, context.Attempt - 1);

        double ms = Math.Clamp(baseMs * factor, MinTimeoutMs, MaxTimeoutMs);
        return TimeSpan.FromMilliseconds(ms);
    }

    public RetryDecision OnResult(RequestObservation observation, Random random)
    {
        bool timedOut = observation.Outcome == RequestOutcome.Timeout;

        // Обучаемся только на первых попытках: их исход (быстрее/медленнее тайм-аута)
        // не зависит от номера попытки и однозначно интерпретируется.
        if (observation.Attempt == 1)
            Learn(timedOut);

        if (!timedOut)
            return RetryDecision.NoRetry();

        if (observation.Attempt >= MaxAttempts)
            return RetryDecision.NoRetry();

        // Бюджет повторов: при массовых тайм-аутах повторы урезаются.
        if (_retryTokens < 1.0)
            return RetryDecision.NoRetry();
        _retryTokens -= 1.0;

        double timeoutMs = Math.Exp(_logTimeout);
        double backoff = 0.25 * Math.Pow(2.0, observation.Attempt - 1);   // 0.25T, 0.5T, 1T
        double overload = 1.0 + 3.0 * _pressure;                         // больше пауза при перегрузке
        double jitter = 0.5 + random.NextDouble();                        // 0.5 .. 1.5
        double delayMs = Math.Min(timeoutMs * backoff * overload * jitter, 2.0 * timeoutMs);

        return RetryDecision.RetryAfter(TimeSpan.FromMilliseconds(Math.Max(0.0, delayMs)));
    }

    private void Learn(bool timedOut)
    {
        _firstAttemptsObserved++;

        // Шаг обучения: большой в начале, затем уменьшается до минимума.
        double rate = Math.Max(
            MinLearningRate,
            InitialLearningRate / Math.Sqrt(1.0 + _firstAttemptsObserved / 10.0));

        // Если тайм-аут -> увеличиваем (сильно), если успех -> слегка уменьшаем.
        // Равновесие: доля тайм-аутов = TargetTimeoutRate.
        double indicator = timedOut ? 1.0 : 0.0;
        _logTimeout += rate * (indicator - TargetTimeoutRate);
        _logTimeout = Math.Clamp(_logTimeout, Math.Log(MinTimeoutMs), Math.Log(MaxTimeoutMs));

        _pressure += PressureSmoothing * (indicator - _pressure);

        _retryTokens = Math.Min(MaxRetryTokens, _retryTokens + RetryTokensPerRequest);
    }
}