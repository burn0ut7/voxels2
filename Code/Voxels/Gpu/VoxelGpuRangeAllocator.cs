internal sealed class VoxelGpuRangeAllocator
{
	private readonly List<VoxelGpuPoolRange> _free = new();

	public int Capacity { get; }
	public int FreeCount => _free.Sum( range => range.Count );
	public int UsedCount => Capacity - FreeCount;
	public int LargestFreeRange => _free.Count == 0 ? 0 : _free.Max( range => range.Count );

	public VoxelGpuRangeAllocator( int capacity )
	{
		if ( capacity < 1 ) throw new System.ArgumentOutOfRangeException( nameof( capacity ) );
		Capacity = capacity;
		_free.Add( new VoxelGpuPoolRange( 0, capacity ) );
	}

	public bool TryAllocate( int count, out VoxelGpuPoolRange allocation )
	{
		if ( count < 0 ) throw new System.ArgumentOutOfRangeException( nameof( count ) );
		if ( count == 0 )
		{
			allocation = default;
			return true;
		}

		for ( var index = 0; index < _free.Count; index++ )
		{
			var range = _free[index];
			if ( range.Count < count ) continue;
			allocation = new VoxelGpuPoolRange( range.Offset, count );
			if ( range.Count == count ) _free.RemoveAt( index );
			else _free[index] = new VoxelGpuPoolRange( range.Offset + count, range.Count - count );
			return true;
		}

		allocation = default;
		return false;
	}

	public void Release( VoxelGpuPoolRange range )
	{
		if ( range.IsEmpty ) return;
		if ( range.Offset < 0 || range.End > Capacity ) throw new System.ArgumentOutOfRangeException( nameof( range ) );
		_free.Add( range );
		CoalesceFreeRanges();
	}

	public void ReleaseBatch( List<VoxelGpuPoolRange> ranges )
	{
		if ( ranges is null || ranges.Count == 0 ) return;
		foreach ( var range in ranges )
		{
			if ( range.IsEmpty ) continue;
			if ( range.Offset < 0 || range.End > Capacity ) throw new System.ArgumentOutOfRangeException( nameof( ranges ) );
			_free.Add( range );
		}
		CoalesceFreeRanges();
	}

	private void CoalesceFreeRanges()
	{
		_free.Sort( (left, right) => left.Offset.CompareTo( right.Offset ) );
		for ( var index = _free.Count - 1; index > 0; index-- )
		{
			var previous = _free[index - 1];
			var current = _free[index];
			if ( previous.End > current.Offset ) throw new System.InvalidOperationException( "GPU pool free ranges overlap." );
			if ( previous.End != current.Offset ) continue;
			_free[index - 1] = new VoxelGpuPoolRange( previous.Offset, previous.Count + current.Count );
			_free.RemoveAt( index );
		}
	}

	public bool Validate( out string failure )
	{
		var total = 0;
		for ( var index = 0; index < _free.Count; index++ )
		{
			var range = _free[index];
			if ( range.Offset < 0 || range.End > Capacity )
			{
				failure = $"free range {index} is outside capacity";
				return false;
			}
			if ( index > 0 && _free[index - 1].End >= range.Offset )
			{
				failure = $"free ranges overlap or are not coalesced at {range.Offset}";
				return false;
			}
			total += range.Count;
		}
		failure = total <= Capacity ? string.Empty : "free range total exceeds capacity";
		return failure.Length == 0;
	}
}
