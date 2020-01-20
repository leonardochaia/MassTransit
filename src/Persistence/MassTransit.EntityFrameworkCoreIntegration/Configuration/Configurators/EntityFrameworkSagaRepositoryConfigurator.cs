namespace MassTransit.EntityFrameworkCoreIntegration.Configurators
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;
    using GreenPipes;
    using MassTransit.Saga;
    using Microsoft.EntityFrameworkCore;
    using Registration;
    using Saga;
    using Saga.Configuration;


    public class EntityFrameworkSagaRepositoryConfigurator<TSaga> :
        IEntityFrameworkSagaRepositoryConfigurator<TSaga>,
        ISpecification
        where TSaga : class, ISaga
    {
        ConcurrencyMode _concurrencyMode;
        Func<IConfigurationServiceProvider, Func<DbContext>> _databaseFactory;
        Func<IQueryable<TSaga>, IQueryable<TSaga>> _queryCustomization;
        ILockStatementProvider _lockStatementProvider;
        IsolationLevel _isolationLevel;
        Action<ISagaRepositoryRegistrationConfigurator<TSaga>> _configureDbContext;

        public EntityFrameworkSagaRepositoryConfigurator()
        {
            _isolationLevel = IsolationLevel.Serializable;
            _concurrencyMode = ConcurrencyMode.Pessimistic;
            _lockStatementProvider = new SqlServerLockStatementProvider();
        }

        public IsolationLevel IsolationLevel
        {
            set => _isolationLevel = value;
        }

        public void CustomizeQuery(Func<IQueryable<TSaga>, IQueryable<TSaga>> queryCustomization)
        {
            _queryCustomization = queryCustomization;
        }

        public ConcurrencyMode ConcurrencyMode
        {
            set => _concurrencyMode = value;
        }

        public ILockStatementProvider LockStatementProvider
        {
            set => _lockStatementProvider = value;
        }

        public void UseDbContext<TContext>(Func<DbContextOptions, TContext> factoryMethod, Func<DbContextOptionsBuilder, DbContextOptionsBuilder> configure)
            where TContext : DbContext
        {
            var dbContextOptionsBuilder = new DbContextOptionsBuilder();

            var builder = configure?.Invoke(dbContextOptionsBuilder) ?? dbContextOptionsBuilder;

            var options = builder.Options;

            _configureDbContext = configurator =>
            {
                configurator.Register(provider => factoryMethod(options));

                configurator.Register<ISagaDbContextFactory<TSaga>, ContainerSagaDbContextFactory<TContext, TSaga>>();
            };
        }


        public void AddExistingDbContext<TContext>()
            where TContext : DbContext
        {
            DatabaseFactory(provider => provider.GetRequiredService<TContext>);
        }

        public void DatabaseFactory(Func<DbContext> databaseFactory)
        {
            DatabaseFactory(_ => databaseFactory);
        }

        public void DatabaseFactory(Func<IConfigurationServiceProvider, Func<DbContext>> databaseFactory)
        {
            _databaseFactory = databaseFactory;

            _configureDbContext = configurator =>
            {
                configurator.Register<ISagaDbContextFactory<TSaga>>(provider => new DelegateSagaDbContextFactory<TSaga>(databaseFactory(provider)));
            };
        }

        public IEnumerable<ValidationResult> Validate()
        {
            if (_configureDbContext == null)
                yield return this.Failure("DbContext", "must be specified");
        }

        public void Register(ISagaRepositoryRegistrationConfigurator<TSaga> configurator)
        {
            _configureDbContext?.Invoke(configurator);

            if (_concurrencyMode == ConcurrencyMode.Optimistic)
                configurator.RegisterSingleInstance(provider => CreateOptimisticLockStrategy());
            else
                configurator.RegisterSingleInstance(provider => CreatePessimisticLockStrategy());

            configurator.RegisterSagaRepository<TSaga, DbContext, EntityFrameworkSagaConsumeContextFactory<TSaga>,
                EntityFrameworkSagaRepositoryContextFactory<TSaga>>();
        }

        ISagaRepositoryLockStrategy<TSaga> CreateOptimisticLockStrategy()
        {
            ILoadQueryProvider<TSaga> queryProvider = new DefaultSagaLoadQueryProvider<TSaga>();
            if (_queryCustomization != null)
                queryProvider = new CustomSagaLoadQueryProvider<TSaga>(queryProvider, _queryCustomization);

            var queryExecutor = new OptimisticLoadQueryExecutor<TSaga>(queryProvider);
            var lockStrategy = new OptimisticSagaRepositoryLockStrategy<TSaga>(queryProvider, queryExecutor, _isolationLevel);

            return lockStrategy;
        }

        ISagaRepositoryLockStrategy<TSaga> CreatePessimisticLockStrategy()
        {
            var statementProvider = _lockStatementProvider ?? new SqlServerLockStatementProvider();

            var queryExecutor = new PessimisticLoadQueryExecutor<TSaga>(statementProvider, _queryCustomization);
            var lockStrategy = new PessimisticSagaRepositoryLockStrategy<TSaga>(queryExecutor, _isolationLevel);

            return lockStrategy;
        }
    }
}