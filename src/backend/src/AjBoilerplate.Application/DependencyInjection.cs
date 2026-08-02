using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Application.Items;
using AjBoilerplate.Application.Messaging;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AjBoilerplate.Application;

/// <summary>Composition helpers for the Application layer.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Every AbstractValidator in this assembly, so a new command's validator is picked up by
        // existing here rather than by being remembered in this file.
        services.AddValidatorsFromAssemblyContaining<CreateItemCommandValidator>();

        // SAMPLE SLICE — delete with the rest of the Item sample.
        services.AddScoped<IItemService, ItemService>();

        services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();

        // Overridden by AddApiAuthentication with the claims-backed actor. Registered here so a host
        // that never wires up authentication still resolves — with a role-less actor that fails
        // every authorization check closed.
        services.AddScoped<ICurrentActor, SystemCurrentActor>();

        return services;
    }
}
