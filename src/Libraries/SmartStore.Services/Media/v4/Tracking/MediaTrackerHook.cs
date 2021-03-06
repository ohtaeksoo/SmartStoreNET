﻿using System;
using System.Collections.Generic;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using SmartStore.ComponentModel;
using SmartStore.Core;
using SmartStore.Core.Data;
using SmartStore.Core.Data.Hooks;
using SmartStore.Data;

namespace SmartStore.Services.Media
{
    [Important]
    public sealed class MediaTrackerHook : DbSaveHook<BaseEntity>
    {   
        // Track items for the current (SaveChanges) unit.
        private readonly HashSet<MediaTrackAction> _actionsUnit = new HashSet<MediaTrackAction>();

        // Track items already processed during the current request.
        private readonly HashSet<MediaTrackAction> _actionsAll = new HashSet<MediaTrackAction>();

        // Entities that are not saved yet but contain effective changes. We won't track if an error occurred during save.
        private readonly IDictionary<BaseEntity, HashSet<MediaTrackAction>> _actionsTemp = new Dictionary<BaseEntity, HashSet<MediaTrackAction>>();

        private readonly Lazy<IMediaTracker> _mediaTracker;
        private readonly IDbContext _dbContext;

        public MediaTrackerHook(Lazy<IMediaTracker> mediaTracker, IDbContext dbContext)
        {
            _mediaTracker = mediaTracker;
            _dbContext = dbContext;
        }

        internal static bool Silent { get; set; }

        protected override void OnUpdating(BaseEntity entity, IHookedEntity entry)
        {
            HookObject(entity, entry, true);
        }

        protected override void OnDeleted(BaseEntity entity, IHookedEntity entry)
        {
            HookObject(entity, entry, false);
        }

        protected override void OnInserted(BaseEntity entity, IHookedEntity entry)
        {
            HookObject(entity, entry, false);
        }

        protected override void OnUpdated(BaseEntity entity, IHookedEntity entry)
        {
            HookObject(entity, entry, false);
        }

        private void HookObject(BaseEntity entity, IHookedEntity entry, bool beforeSave)
        {
            if (Silent)
                return;
            
            var type = entry.EntityType;

            if (!_mediaTracker.Value.TryGetTrackedPropertiesFor(type, out var properties))
            {
                throw new NotSupportedException();
            }

            var state = entry.InitialState;

            foreach (var prop in properties)
            {
                if (beforeSave)
                {
                    if (entry.Entry.TryGetModifiedProperty(_dbContext, prop.Name, out object prevValue))
                    {
                        var actions = new HashSet<MediaTrackAction>();
                        
                        // Untrack the previous file relation (if not null)
                        TryAddTrack(prop.Album, entry.Entity, prevValue, MediaTrackOperation.Untrack, actions);

                        // Track the new file relation (if not null)
                        TryAddTrack(prop.Album, entry.Entity, entry.Entry.CurrentValues[prop.Name], MediaTrackOperation.Track, actions);

                        _actionsTemp[entry.Entity] = actions;
                    }
                }
                else
                {
                    switch (state)
                    {
                        case EntityState.Added:
                        case EntityState.Deleted:
                            var value = FastProperty.GetProperty(type, prop.Name).GetValue(entry.Entity);
                            TryAddTrack(prop.Album, entry.Entity, value, state == EntityState.Added ? MediaTrackOperation.Track : MediaTrackOperation.Untrack);
                            break;
                        case EntityState.Modified:
                            if (_actionsTemp.TryGetValue(entry.Entity, out var actions))
                            {
                                _actionsUnit.AddRange(actions);
                            }
                            break;
                    }
                }
            }
        }

        private void TryAddTrack(string album, BaseEntity entity, object value, MediaTrackOperation operation, HashSet<MediaTrackAction> actions = null)
        {
            if (value == null)
                return;

            if ((int)value > 0)
            {
                (actions ?? _actionsUnit).Add(new MediaTrackAction 
                { 
                    Album = album,
                    EntityId = entity.Id, 
                    EntityName = entity.GetEntityName(), 
                    MediaFileId = (int)value, Operation = operation 
                });
            }
        }

        public override void OnAfterSaveCompleted()
        {
            // Remove already processed items during this request.
            _actionsUnit.ExceptWith(_actionsAll);

            if (_actionsUnit.Count == 0)
            {
                return;
            }

            _actionsAll.UnionWith(_actionsUnit);

            // Commit all track items in one go
            var tracker = _mediaTracker.Value;
            using (tracker.BeginScope(false))
            {
                // TODO: (mm) make media setting: MakeFilesTransientWhenOrphaned
                tracker.TrackMany(_actionsUnit);
            }

            _actionsUnit.Clear();
            _actionsTemp.Clear();
        }
    }
}
