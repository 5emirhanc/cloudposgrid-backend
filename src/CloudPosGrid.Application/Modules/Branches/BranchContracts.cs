namespace CloudPosGrid.Application.Modules.Branches;

public record BranchDto(Guid Id, string Name, string? Address, string? Phone, bool IsActive, bool IsDefault);
public record CreateBranchRequest(string Name, string? Address, string? Phone);
public record UpdateBranchRequest(string Name, string? Address, string? Phone, bool IsActive);

public interface IBranchService
{
    Task<List<BranchDto>> GetAllAsync(CancellationToken ct = default);
    Task<BranchDto> CreateAsync(CreateBranchRequest req, CancellationToken ct = default);
    Task<BranchDto> UpdateAsync(Guid id, UpdateBranchRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
