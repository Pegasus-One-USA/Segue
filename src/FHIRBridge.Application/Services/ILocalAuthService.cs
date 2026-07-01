using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface ILocalAuthService
{
    Task<LocalLoginResponse> LoginAsync(LocalLoginRequest request, CancellationToken cancellationToken);

    Task<LocalLoginResponse> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken);

    Task<ForgotPasswordResponse> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken);

    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken);
}
