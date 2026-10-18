using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TravelPlanner.Admin.Models;
using TravelPlanner.Admin.Services;

namespace TravelPlanner.Admin.Pages.Explore;

public sealed class EditModel(SupabaseAdminClient supabase) : PageModel
{
    [BindProperty] public ExploreForm Form { get; set; } = new();
    [BindProperty] public bool IsNew { get; set; } = true;
    [BindProperty] public IFormFile? Cover { get; set; }
    [BindProperty] public IFormFile? Thumb { get; set; }

    public string? Error { get; private set; }
    public SupabaseAdminClient Supabase => supabase;

    public async Task<IActionResult> OnGetAsync(string? slug, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(slug))
            return Page();
        try
        {
            if (await supabase.RpcAsync("admin_explore_get", new { p_slug = slug }, ct) is not JsonObject json)
                return NotFound();
            Form = ExploreForm.FromJson(json);
            IsNew = false;
        }
        catch (SupabaseException e)
        {
            Error = e.Message;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        foreach (var (field, message) in Form.Validate())
            ModelState.AddModelError($"{nameof(Form)}.{field}", message);
        foreach (var (file, folder) in new[] { (Cover, "covers"), (Thumb, "thumbs") })
            if (file is not null && ImageError(file) is { } error)
                ModelState.AddModelError(folder == "covers" ? nameof(Cover) : nameof(Thumb), error);
        if (!ModelState.IsValid)
            return Page();

        try
        {
            // a new plan must not silently replace an existing one with the same slug
            if (IsNew && await supabase.RpcAsync("admin_explore_get", new { p_slug = Form.Slug.Trim() }, ct) is not null)
            {
                ModelState.AddModelError($"{nameof(Form)}.{nameof(Form.Slug)}", "Bu slug zaten var.");
                return Page();
            }
            if (Cover is not null)
                Form.CoverPath = await UploadAsync(Cover, "covers", ct);
            if (Thumb is not null)
                Form.ThumbPath = await UploadAsync(Thumb, "thumbs", ct);
            await supabase.RpcAsync("admin_explore_upsert", new { p = Form.ToJson() }, ct);
        }
        catch (SupabaseException e)
        {
            Error = e.Message;
            return Page();
        }
        TempData["Message"] = "Kaydedildi.";
        return RedirectToPage(new { slug = Form.Slug.Trim() });
    }

    private async Task<string> UploadAsync(IFormFile file, string folder, CancellationToken ct)
    {
        var path = $"{folder}/{Form.Slug.Trim()}.{(file.ContentType == "image/webp" ? "webp" : "jpg")}";
        await using var stream = file.OpenReadStream();
        await supabase.UploadImageAsync(path, stream, file.ContentType, ct);
        return path;
    }

    internal static string? ImageError(IFormFile file) =>
        file.ContentType is not ("image/webp" or "image/jpeg") ? "Yalnızca WebP veya JPEG."
        : file.Length > SupabaseAdminClient.MaxImageBytes ? "En fazla 1 MB."
        : file.Length == 0 ? "Dosya boş."
        : null;
}
