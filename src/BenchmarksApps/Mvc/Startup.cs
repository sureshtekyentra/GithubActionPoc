using System;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mvc
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
            UseNewtonsoftJson = Configuration["UseNewtonsoftJson"] == "true";
        }

        public IConfiguration Configuration { get; }

        bool UseNewtonsoftJson { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            var mvcBuilder = services.AddControllers();

            if (UseNewtonsoftJson)
            {
                mvcBuilder.AddNewtonsoftJson();
            }
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
        {
            if (UseNewtonsoftJson)
            {
                logger.LogInformation("MVC is configured to use Newtonsoft.Json.");
            }

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                MapAction(endpoints, "/EchoAction", (Person person) => person);

                endpoints.MapControllers();
            });
        }

        public static IEndpointConventionBuilder MapAction<TIn, TOut>(IEndpointRouteBuilder endpoints, string pattern, Func<TIn, TOut> action)
        {
            return endpoints.MapPost(pattern, async httpContext =>
            {
                httpContext.Response.Headers["Content-Type"] = "application/json; charset=utf-8";

                var input = await JsonSerializer.DeserializeAsync<TIn>(httpContext.Request.Body);
                var output = action(input);
                await JsonSerializer.SerializeAsync(httpContext.Response.Body, output);
            });
        }
    }

    public class Person
    {
        public string Name { get; set; }
    }

    [ApiController]
    public class EchoController : ControllerBase
    {
        [HttpPost("/EchoController")]
        public Person Get(Person person) => person;
    }
}
