using System.Collections.Generic;
using System.Text.Json.Serialization;
using Bugsnag.AspNet.Core;
using ChicksGold.Server.Authentication;
using ChicksGold.Data.Models.Profiles;
using ChicksGold.Data.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerUI;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ChicksGold.Server.Lib.Extensions;
using ChicksGold.Server.Services;
using ChicksGold.Server.ThirdPartyServices;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Principal;
using Redis;
using StackExchange.Redis;
using System.Threading;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Swashbuckle.AspNetCore.SwaggerGen;
using Microsoft.ApplicationInsights.AspNetCore.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using System.Linq;
using ChicksGold.Server.Lib.Middleware;

namespace PdfApi
{
	public class Startup(IConfiguration configuration)
	{
		private IConfiguration Configuration { get; } = configuration;
		private const string CorsPolicy = "CustomCORSPolicyForAuthentication";

		public void ConfigureServices(IServiceCollection services)
		{
			services.AddCors(
				o => o.AddPolicy(CorsPolicy, builder =>
				{
					builder
						.AllowAnyOrigin()
						.AllowAnyHeader()
						.AllowAnyMethod()
						.AllowCredentials();
				}));
			services.AddDatabase(Configuration);
			services.AddHealthChecks();
			services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
			services.AddScoped<UserResolver>();
			services.AddScoped<IPdfService, PdfService>();
			services.AddScoped<IPrincipal>(context => context.GetService<IHttpContextAccessor>()?.HttpContext?.User);
			services.RegisterScopedServices();
			services.RegisterScopedRepositories();
			services.RegisterThirdPartyHttpClients();

#if !DEBUG
			services.RegisterJwtWithRedis();

			services.AddSingleton<IConnectionMultiplexer>(provider =>
			{
				var connectionString = Configuration.RedisConnectionString();
				return ConnectionMultiplexer.Connect(connectionString);
			});
			services.AddDistributedMemoryCache();
#endif
#if DEBUG
		services.AddDistributedMySqlCache(options =>
			{
				options.ConnectionString = Configuration.DbConnectionString();
				options.TableName = "LocalCache";
			});
			services.RegisterJwtWithDistributedCache();
#endif
			services.AddMemoryCache();
			services.AddAutoMapper(typeof(UserProfile));

			AuthenticationHelper.AddAuthentication(services, Configuration);

			services
				.AddControllers()
				.AddJsonOptions(o =>
				{
					o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
				});

			services.AddApplicationInsightsTelemetry(options => options.ConnectionString = configuration.TelemetryKey());

			services.AddRouting();
			services.AddEndpointsApiExplorer();
			services.AddBugsnag(c => c.ApiKey = configuration.GetBugsnagApiKey());

#if (DEBUG || DEVELOPMENT || STAGING)
			services.AddSwaggerGen(SetupSwaggerGen);
#endif
		}

		public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
		{
#if (DEBUG || DEVELOPMENT || STAGING)
			app.UseDeveloperExceptionPage();
			app.UseMiddleware<SwaggerBasicAuthMiddleware>();
			app.UseSwagger();
			app.UseSwaggerUI(SetupSwaggerUI);
#endif
			app.UseRouting();
			app.UseAuthentication();
			app.UseAuthorization();

			app.UseCors(CorsPolicy);

			app.UseEndpoints(endpoints =>
			{
				endpoints.MapControllers();
				endpoints.MapHealthChecks("/Health", new HealthCheckOptions
				{
					ResultStatusCodes =
					{
						[HealthStatus.Healthy] = StatusCodes.Status200OK,
						[HealthStatus.Degraded] = StatusCodes.Status200OK,
						[HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
					},
					AllowCachingResponses = true
				});
			});
		}

#region Function definitions

		static void SetupSwaggerGen(SwaggerGenOptions options)
		{
			options.SwaggerDoc("v1", new OpenApiInfo { Title = "Swagger", Version = "v1" });

			options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
			{
				Description = @"JWT Authorization header using the Bearer scheme.
                    Enter 'Bearer' [space] and then your token in the text input below.
                    Example: 'Bearer 12345abcdef'",
				Name = "Authorization",
				In = ParameterLocation.Header,
				Type = SecuritySchemeType.ApiKey,
				Scheme = "Bearer"
			});

			options.AddSecurityRequirement(new OpenApiSecurityRequirement
			{
				{
					new OpenApiSecurityScheme
					{
						Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
						Scheme = "oauth2",
						Name = "Bearer",
						In = ParameterLocation.Header
					},
					new List<string>()
				}
			});
		}

		static void SetupSwaggerUI(SwaggerUIOptions options)
		{
			options.SwaggerEndpoint("/swagger/v1/swagger.json", "Swagger");
			options.DocExpansion(DocExpansion.None);
			options.EnableDeepLinking();
			options.EnableValidator();
		}
#endregion
	}
}
