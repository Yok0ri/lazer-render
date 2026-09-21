using LazerRender.Api.Services;
using LazerRender.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LazerRender.Api.Controllers;

[ApiController]
[Route("/api/v1")]
[Authorize]
public sealed class MetaController : ControllerBase
{
    private readonly EncoderResolver encoderResolver;

    public MetaController(EncoderResolver encoderResolver)
    {
        this.encoderResolver = encoderResolver;
    }

    [HttpGet("capabilities")]
    public IActionResult Capabilities()
    {
        var encoder = encoderResolver.Resolve();
        return Ok(new
        {
            encoder = encoder.Display(),
            autoDetected = encoderResolver.IsAuto,
        });
    }

    /// <summary>
    /// The authoritative default render configuration, used by the UI for reset-to-default controls.
    /// </summary>
    [HttpGet("render-config/defaults")]
    public IActionResult Defaults() => Ok(new RenderConfig());
}
