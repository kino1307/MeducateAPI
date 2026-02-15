using System.ComponentModel.DataAnnotations;

namespace Meducate.Application.DTOs;

internal sealed record RegisterRequest
{
    [Required, EmailAddress, StringLength(320)]
    public string Email { get; init; } = string.Empty;
}
