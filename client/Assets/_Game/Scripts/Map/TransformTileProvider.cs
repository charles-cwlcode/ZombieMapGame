using Mapbox.Example.Scripts.TileProviderBehaviours;
using Mapbox.UnityMapService.TileProviders;
using UnityEngine;

namespace ZombieMap.Client.Map
{
    /// <summary>Loads map tiles in a square around a transform (the player marker).</summary>
    public sealed class TransformTileProvider : TileProviderBehaviour
    {
        public TransformBasedTileProvider TileProvider = new TransformBasedTileProvider();

        public override TileProvider Core
        {
            get
            {
                if (TileProvider.Transform == null) TileProvider.Transform = transform;
                return TileProvider;
            }
        }
    }
}
