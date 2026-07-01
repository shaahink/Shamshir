using Microsoft.AspNetCore.Mvc;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/ctrader/listen")]
public class CtraderListenController : ControllerBase
{
    private readonly CTraderListenService _listenService;
    private readonly ILogger<CtraderListenController> _logger;

    public CtraderListenController(
        CTraderListenService listenService,
        ILogger<CtraderListenController> logger)
    {
        _listenService = listenService;
        _logger = logger;
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] CtraderListenConfig config, CancellationToken ct)
    {
        try
        {
            await _listenService.StartListeningAsync(config, ct);
            return Ok(new
            {
                status = "listening",
                dataPort = _listenService.DataPort,
                commandPort = _listenService.CommandPort,
                message = "Engine listening. In cTrader Desktop: add TradingEngineCBot to chart, " +
                          $"set DataPort={_listenService.DataPort} CommandPort={_listenService.CommandPort}, " +
                          "grant Full Access, then run a backtest."
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start cTrader listener");
            return StatusCode(500, new { error = "Failed to start listener." });
        }
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop(CancellationToken ct)
    {
        try
        {
            await _listenService.StopListeningAsync(ct);
            return Ok(new { status = "idle" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop cTrader listener");
            return StatusCode(500, new { error = "Failed to stop listener." });
        }
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(new
        {
            state = _listenService.State.ToString().ToLowerInvariant(),
            isListening = _listenService.IsListening,
            activeRunId = _listenService.ActiveRunId,
            dataPort = _listenService.DataPort,
            commandPort = _listenService.CommandPort
        });
    }
}
