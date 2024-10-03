using Microsoft.AspNetCore.Builder;

public static class GlobalRoutePrefixMiddlewareExtensions
{
	public static IApplicationBuilder UseGlobalRoutePrefix(
		this IApplicationBuilder builder, string v)
	{
		return builder.UseMiddleware<GlobalRoutePrefixMiddleware>(v);
	}
}
