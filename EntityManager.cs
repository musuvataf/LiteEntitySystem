using System;
using System.Collections.Generic;
using System.Diagnostics;
using K4os.Compression.LZ4;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public delegate T EntityConstructor<out T>(EntityParams entityParams) where T : InternalEntity;
    
    [Flags]
    public enum ExecuteFlags : byte
    {
        SendToOwner = 1,
        SendToOther = 1 << 1,
        ExecuteOnPrediction = 1 << 2,
        ExecuteOnServer = 1 << 3,
        ExecuteOnClinet = 1 << 4,
        All = SendToOther | SendToOwner | ExecuteOnPrediction | ExecuteOnServer
    }

    public enum NetworkMode : byte
    {
        Client,
        Server
    }

    public enum UpdateMode
    {
        Normal,
        PredictionRollback
    }

    public abstract class EntityTypesMap
    {
        internal ushort MaxId;
        internal readonly Dictionary<Type, (ushort, EntityConstructor<InternalEntity>)> RegisteredTypes = new Dictionary<Type, (ushort, EntityConstructor<InternalEntity>)>();
    }

    /// <summary>
    /// Entity types map that will be used for EntityManager
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public sealed class EntityTypesMap<T> : EntityTypesMap where T : Enum
    {
        /// <summary>
        /// Register new entity type that will be used in game
        /// </summary>
        /// <param name="id">Enum value that will describe entity class id</param>
        /// <param name="constructor">Constructor of entity</param>
        /// <typeparam name="TEntity">Type of entity</typeparam>
        public EntityTypesMap<T> Register<TEntity>(T id, EntityConstructor<TEntity> constructor) where TEntity : InternalEntity 
        {
            ushort classId = (ushort)((ushort)(object)id + 1);
            EntityClassInfo<TEntity>.ClassId = classId;
            RegisteredTypes.Add(typeof(TEntity), (classId, constructor));
            MaxId = Math.Max(MaxId, classId);
            return this;
        }
    }
    
    /// <summary>
    /// Base class for client and server manager
    /// </summary>
    public abstract class EntityManager
    {
        /// <summary>
        /// Maximum synchronized (without LocalOnly) entities
        /// </summary>
        public const int MaxSyncedEntityCount = 8192;
        
        /// <summary>
        /// Invalid entity id
        /// </summary>
        public const ushort InvalidEntityId = ushort.MaxValue;
        
        /// <summary>
        /// Total entities count (including local)
        /// </summary>
        public ushort EntitiesCount { get; private set; }
        
        /// <summary>
        /// Current tick
        /// </summary>
        public ushort Tick { get; private set; }

        /// <summary>
        /// Interpolation time between logic and render
        /// </summary>
        public float LerpFactor => _accumulator / (float)_deltaTimeTicks;
        
        /// <summary>
        /// Current update mode (can be used inside entities to separate logic for rollbacks)
        /// </summary>
        public UpdateMode UpdateMode { get; protected set; }
        
        /// <summary>
        /// Current mode (Server or Client)
        /// </summary>
        public readonly NetworkMode Mode;
        
        /// <summary>
        /// Is server
        /// </summary>
        public readonly bool IsServer;

        /// <summary>
        /// Is client
        /// </summary>
        public readonly bool IsClient;
        
        /// <summary>
        /// FPS of game logic
        /// </summary>
        public readonly int FramesPerSecond;
        
        /// <summary>
        /// Fixed delta time
        /// </summary>
        public readonly double DeltaTime;

        /// <summary>
        /// Fixed delta time (float for less precision)
        /// </summary>
        public readonly float DeltaTimeF;

        /// <summary>
        /// Local player id (0 on server)
        /// </summary>
        public byte PlayerId => InternalPlayerId;

        protected const byte PacketDiffSync = 1;
        protected const byte PacketClientSync = 2;
        protected const byte PacketBaselineSync = 3;
        protected const byte PacketDiffSyncLast = 4;
        protected const int MaxFieldSize = 1024;
        protected const int MaxSavedStateDiff = 6;
        internal const int MaxParts = 256;
        private const int MaxTicksPerUpdate = 5;

        public double VisualDeltaTime { get; private set; }
        
        protected int MaxEntityId = -1; //current maximum id
        
        protected readonly EntityFilter<InternalEntity> AliveEntities = new EntityFilter<InternalEntity>();

        private readonly double _stopwatchFrequency;
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly Queue<ushort> _localIdQueue = new Queue<ushort>();
        private readonly SingletonEntityLogic[] _singletonEntities;
        private readonly EntityFilter[] _entityFilters;
        private readonly Dictionary<Type, ushort> _registeredTypeIds = new Dictionary<Type, ushort>();
        internal readonly InternalEntity[] EntitiesDict = new InternalEntity[MaxSyncedEntityCount];
        internal readonly EntityClassData[] ClassDataDict;

        private readonly long _deltaTimeTicks;
        private long _accumulator;
        private long _lastTime;
        private ushort _localIdCounter = MaxSyncedEntityCount;

        internal byte InternalPlayerId;

        protected EntityManager(EntityTypesMap typesMap, NetworkMode mode, byte framesPerSecond)
        {
#if UNITY_ANDROID
            if (IntPtr.Size == 4)
                LZ4Codec.Enforce32 = true;
#endif

            Interpolation.Register<float>(Utils.Lerp);
            Interpolation.Register<FloatAngle>(FloatAngle.Lerp);
            ClassDataDict = new EntityClassData[typesMap.MaxId+1];

            ushort filterCount = 0;
            ushort singletonCount = 0;
            
            //preregister some types
            _registeredTypeIds.Add(typeof(HumanControllerLogic), filterCount++);
            
            foreach (var kv in typesMap.RegisteredTypes)
            {
                var entType = kv.Key;
                (ushort classId, var entityConstructor) = kv.Value;

                ClassDataDict[classId] = new EntityClassData(
                    entType.IsSubclassOf(typeof(SingletonEntityLogic)) ? singletonCount++ : filterCount++, 
                    entType, 
                    classId, 
                    entityConstructor);
                _registeredTypeIds.Add(entType, ClassDataDict[classId].FilterId);
                Logger.Log($"Register: {entType.Name} ClassId: {classId})");
            }

            foreach (var registeredType in typesMap.RegisteredTypes.Values)
            {
                //map base ids
                ClassDataDict[registeredType.Item1].PrepareBaseTypes(_registeredTypeIds, ref singletonCount, ref filterCount);
            }

            _entityFilters = new EntityFilter[filterCount];
            _singletonEntities = new SingletonEntityLogic[singletonCount];
            
            Mode = mode;
            IsServer = Mode == NetworkMode.Server;
            IsClient = Mode == NetworkMode.Client;
            FramesPerSecond = framesPerSecond;
            DeltaTime = 1.0 / framesPerSecond;
            DeltaTimeF = (float) DeltaTime;
            _stopwatchFrequency = 1.0 / Stopwatch.Frequency;
            _deltaTimeTicks = (long)(DeltaTime * Stopwatch.Frequency);
        }

        /// <summary>
        /// Remove all entities and reset all counters and timers
        /// </summary>
        public void Reset()
        {
            EntitiesCount = 0;

            Tick = 0;
            VisualDeltaTime = 0.0;
            _accumulator = 0;
            _lastTime = 0;
            InternalPlayerId = 0;
            _localIdCounter = MaxSyncedEntityCount;
            _localIdQueue.Clear();
            _stopwatch.Restart();

            AliveEntities.Clear();

            for (int i = 0; i < _singletonEntities.Length; i++)
                _singletonEntities[i] = null;
            for (int i = 0; i <= MaxEntityId; i++)
                EntitiesDict[i] = null;
            for (int i = 0; i < _entityFilters.Length; i++)
                _entityFilters[i] = null;
        }

        /// <summary>
        /// Get entity by id
        /// throws exception if entity is null or invalid type
        /// </summary>
        /// <param name="id">Id of entity</param>
        /// <returns>Entity if it exists, null if id == InvalidEntityId</returns>
        public T GetEntityById<T>(ushort id) where T : InternalEntity
        {
            return id == InvalidEntityId ? null : (T)EntitiesDict[id];
        }
        
        /// <summary>
        /// Get entity by id
        /// </summary>
        /// <param name="id">Id of entity</param>
        /// <returns>Entity if it exists, null otherwise</returns>
        public T GetEntityByIdSafe<T>(ushort id) where T : InternalEntity
        {
            return id == InvalidEntityId ? null : EntitiesDict[id] as T;
        }

        /// <summary>
        /// Get all entities with type
        /// </summary>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Entity filter that can be used in foreach</returns>
        public EntityFilter<T> GetEntities<T>() where T : EntityLogic
        {
            ref var entityFilter = ref _entityFilters[_registeredTypeIds[typeof(T)]];
            if (entityFilter != null)
                return (EntityFilter<T>)entityFilter;

            //initialize new
            var typedFilter = new EntityFilter<T>();
            entityFilter = typedFilter;
            for (int i = 0; i <= MaxEntityId; i++)
            {
                if(EntitiesDict[i] is T castedEnt && !castedEnt.IsDestroyed)
                    typedFilter.Add(castedEnt);
            }

            return typedFilter;
        }
        
        /// <summary>
        /// Get all controller entities with type
        /// </summary>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Entity filter that can be used in foreach</returns>
        public EntityFilter<T> GetControllers<T>() where T : ControllerLogic
        {
            ref var entityFilter = ref _entityFilters[_registeredTypeIds[typeof(T)]];
            if (entityFilter != null)
                return (EntityFilter<T>)entityFilter;
            
            var typedFilter = new EntityFilter<T>();
            entityFilter = typedFilter;
            for (int i = 0; i <= MaxEntityId; i++)
            {
                if(EntitiesDict[i] is T castedEnt)
                    typedFilter.Add(castedEnt);
            }

            return typedFilter;
        }

        /// <summary>
        /// Get existing singleton entity
        /// </summary>
        /// <typeparam name="T">Singleton entity type</typeparam>
        /// <returns>Singleton entity, can throw exceptions on invalid type</returns>
        public T GetSingleton<T>() where T : SingletonEntityLogic
        {
            return (T)_singletonEntities[_registeredTypeIds[typeof(T)]];
        }

        /// <summary>
        /// Get singleton entity
        /// </summary>
        /// <typeparam name="T">Singleton entity type</typeparam>
        /// <returns>Singleton entity or null if it didn't exists</returns>
        public T GetSingletonSafe<T>() where T : SingletonEntityLogic
        {
            return _singletonEntities[_registeredTypeIds[typeof(T)]] as T;
        }

        /// <summary>
        /// Is singleton exists and has correct type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public bool HasSingleton<T>() where T : SingletonEntityLogic
        {
            return _singletonEntities[_registeredTypeIds[typeof(T)]] is T;
        }

        /// <summary>
        /// Try get singleton entity
        /// </summary>
        /// <param name="singleton">result singleton entity</param>
        /// <typeparam name="T">Singleton type</typeparam>
        /// <returns>true if entity exists</returns>
        public bool TryGetSingleton<T>(out T singleton) where T : SingletonEntityLogic
        {
            var s = _singletonEntities[_registeredTypeIds[typeof(T)]];
            if (s != null)
            {
                singleton = (T)s;
                return true;
            }
            singleton = null;
            return false;
        }
        
        /// <summary>
        /// Add local entity that will be not syncronized
        /// </summary>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null if entities limit is reached (65535 - <see cref="MaxSyncedEntityCount"/>)</returns>
        public T AddLocalEntity<T>(Action<T> initMethod = null) where T : InternalEntity
        {
            if (_localIdCounter == 0)
            {
                Logger.LogError("Max local entities count reached");
                return null;
            }

            var classId = EntityClassInfo<T>.ClassId;
            var entityParams = new EntityParams(classId, _localIdQueue.Count > 0 ? _localIdQueue.Dequeue() : _localIdCounter++, 0, this);
            var entity = (T)ClassDataDict[classId].EntityConstructor(entityParams);
            EntitiesCount++;

            if (IsClient && entity is EntityLogic logic)
            {
                logic.InternalOwnerId = InternalPlayerId;
            }
            initMethod?.Invoke(entity);
            ConstructEntity(entity);
            return entity;
        }

        protected InternalEntity AddEntity(EntityParams entityParams)
        {
            if (entityParams.Id >= InvalidEntityId)
            {
                throw new ArgumentException($"Invalid entity id: {entityParams.Id}");
            }
            var entity = ClassDataDict[entityParams.ClassId].EntityConstructor(entityParams);
            MaxEntityId = MaxEntityId < entityParams.Id ? entityParams.Id : MaxEntityId;
            EntitiesDict[entity.Id] = entity;
            EntitiesCount++;
            return entity;
        }

        protected void ConstructEntity(InternalEntity e)
        {
            e.OnConstructed();
            ref var classData = ref ClassDataDict[e.ClassId];
            if (classData.IsSingleton)
            {
                _singletonEntities[classData.FilterId] = (SingletonEntityLogic)e;
                foreach (int baseId in classData.BaseIds)
                    _singletonEntities[baseId] = (SingletonEntityLogic)e;
            }
            else
            {
                _entityFilters[classData.FilterId]?.Add(e);
                foreach (int baseId in classData.BaseIds)
                    _entityFilters[baseId]?.Add(e);
            }
            if (classData.IsUpdateable && (IsServer || e.IsLocal || (IsClient && classData.UpdateOnClient)))
                AliveEntities.Add(e);
        }

        internal void RemoveEntity(InternalEntity e)
        {
            ref var classData = ref ClassDataDict[e.ClassId];
            _entityFilters[classData.FilterId]?.Remove(e);
            foreach (int baseId in classData.BaseIds)
                _entityFilters[baseId]?.Remove(e);
            if (classData.IsUpdateable && (IsServer || e.IsLocal || (IsClient && classData.UpdateOnClient)))
                AliveEntities.Remove(e);
            if (classData.IsLocalOnly)
                _localIdQueue.Enqueue(e.Id);
            EntitiesCount--;
            //Logger.Log($"{Mode} - RemoveEntity: {e.Id}");
        }

        protected abstract void OnLogicTick();

        /// <summary>
        /// Main update method, updates internal fixed timer and do all other stuff
        /// </summary>
        public virtual void Update()
        {
            if(!_stopwatch.IsRunning)
                _stopwatch.Start();
            
            long elapsedTicks = _stopwatch.ElapsedTicks;
            long ticksDelta = elapsedTicks - _lastTime;
            VisualDeltaTime = ticksDelta * _stopwatchFrequency;
            _accumulator += ticksDelta;
            _lastTime = elapsedTicks;

            int updates = 0;
            while (_accumulator >= _deltaTimeTicks)
            {
                //Lag
                if (updates >= MaxTicksPerUpdate)
                {
                    _accumulator = 0;
                    return;
                }
                Tick++;
                OnLogicTick();
                _accumulator -= _deltaTimeTicks;
                updates++;
            }
        }
    }
}
