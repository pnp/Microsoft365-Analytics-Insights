using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Web.AnalyticsWeb;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddControllersWithViews();
    //    builder.Services
    //.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    //.AddMicrosoftIdentityWebApi((options) => { }, (options) =>
    //{
    //    options.ClientId = config.AzureB2C.ClientId;
    //    options.TenantId = config.AzureB2C.TenantId;
    //    options.Instance = config.AzureB2C.Instance;
    //    options.Domain = config.AzureB2C.Domain;
    //    options.SignUpSignInPolicyId = config.AzureB2C.SignInPolicy;
    //});

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy
                    .WithOrigins("https://www.test-cors.org")
                    .WithMethods("GET", "OPTIONS");
            });
        });

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Home/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();

        app.UseRouting();

        app.UseAuthorization();

        app.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}");

        app.Run();
    }
}
