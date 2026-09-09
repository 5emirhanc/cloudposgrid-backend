using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Stock;

public sealed class CategoryService : ICategoryService
{
    private readonly IApplicationDbContext _db;

    public CategoryService(IApplicationDbContext db) => _db = db;

    public async Task<List<CategoryDto>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Categories
            .OrderBy(c => c.Name)
            .Select(c => new CategoryDto(c.Id, c.Name, c.IsActive, c.Products.Count))
            .ToListAsync(ct);

    public async Task<CategoryDto> CreateAsync(CreateCategoryRequest req, CancellationToken ct = default)
    {
        var category = new Category { Name = req.Name.Trim() };
        _db.Categories.Add(category);
        await _db.SaveChangesAsync(ct);
        return new CategoryDto(category.Id, category.Name, category.IsActive, 0);
    }

    public async Task<CategoryDto> UpdateAsync(Guid id, UpdateCategoryRequest req, CancellationToken ct = default)
    {
        var category = await _db.Categories.FindAsync([id], ct)
            ?? throw NotFoundException.For("Kategori", id);

        category.Name = req.Name.Trim();
        category.IsActive = req.IsActive;
        await _db.SaveChangesAsync(ct);

        var count = await _db.Products.CountAsync(p => p.CategoryId == id, ct);
        return new CategoryDto(category.Id, category.Name, category.IsActive, count);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var category = await _db.Categories.FindAsync([id], ct)
            ?? throw NotFoundException.For("Kategori", id);

        // İlişkili ürünlerin CategoryId'si null'a çekilir (OnDelete: SetNull).
        _db.Categories.Remove(category);
        await _db.SaveChangesAsync(ct);
    }
}
