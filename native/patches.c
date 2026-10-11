#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <stdlib.h>
#include <emmintrin.h>
#include <windows.h>
#include <stdbool.h>

#define API __declspec(dllexport)

enum
{
	ARRAY_LENGTH_OFFSET = 0x18,
	ARRAY_DATA_OFFSET = 0x20,
	CLASS_VTABLE_OFFSET = 0x138,
	VIRTUAL_METHOD_SIZE = 16
};

#define FIELD(type, object, offset) (*(type*)((char*)(object) + (offset)))

static __forceinline float scalarAdd(float a, float b)
{
	return _mm_cvtss_f32(_mm_add_ss(_mm_set_ss(a), _mm_set_ss(b)));
}

static __forceinline float scalarSub(float a, float b)
{
	return _mm_cvtss_f32(_mm_sub_ss(_mm_set_ss(a), _mm_set_ss(b)));
}

static __forceinline float scalarMultiply(float a, float b)
{
	return _mm_cvtss_f32(_mm_mul_ss(_mm_set_ss(a), _mm_set_ss(b)));
}

static __forceinline int32_t wrappingAddInt32(int32_t a, int32_t b)
{
	return (int32_t)((uint32_t)a + (uint32_t)b);
}

typedef struct Config
{
	void* object_new;
	void* handle_new;
	void* weak_new;
	void* handle_target;
	void* handle_free;
	void* class_size;
	void* write_barrier;
	void* comparer_class;
	void* comparer_ctor;
	void* list_enumerator;
	void* lock_enumerator_ctor;
	void* buff_add_range;
	void* lock_enumerator_original;
} Config;

static Config config;
typedef void* (*ObjectNew)(void*);
typedef uintptr_t GcHandle;
_Static_assert(sizeof(GcHandle) == sizeof(void*), "GC handle must be pointer-sized");
typedef GcHandle (*HandleNew)(void*, bool);
typedef void* (*HandleTarget)(GcHandle);
typedef void (*HandleFree)(GcHandle);

static void storeReference(void* owner, void** address, void* value)
{
	// il2cpp_gc_wbarrier_set_field marks the slot; it does not perform the store.
	*address = value;
	((void (*)(void*, void**, void*))config.write_barrier)(owner, address, value);
}

static __declspec(thread) bool configured_thread = false;
API void Configure(const Config* c)
{
	config = *c;
	configured_thread = true;
}

typedef struct PoolSlot
{
	void* klass;
	void* object;
	GcHandle handle;
	uint32_t bytes;
	bool busy;
} PoolSlot;

static __declspec(thread) PoolSlot pool[256];
static __declspec(thread) uint32_t pool_capacity = 32;
static __declspec(thread) bool retain_pool_classes = false;
static __declspec(thread) bool warm_scoped_pools = false;
API void ConfigureWarmScopedPools(int32_t enabled)
{
	warm_scoped_pools = enabled != 0;
}

typedef struct ConsumeScope ConsumeScope;
static __declspec(thread) ConsumeScope* current_scope = NULL;

static void clearSlot(PoolSlot* s)
{
	for (uint32_t o = 16; o + 8 <= s->bytes; o += 8)
		storeReference(s->object, (void**)((char*)s->object + o), NULL);
	if ((s->bytes - 16) % 8)
		memset((char*)s->object + (s->bytes & ~7u), 0, s->bytes % 8);
	s->busy = false;
}

static void* acquire(void* klass, PoolSlot** slot)
{
	*slot = NULL;
	if (!configured_thread)
		return ((ObjectNew)config.object_new)(klass);
	for (PoolSlot *s = pool, *s_end = pool + pool_capacity; s < s_end; ++s)
		if (!s->busy && s->klass == klass)
		{
			s->busy = true;
			*slot = s;
			return s->object;
		}
	PoolSlot* destination = NULL;
	if (retain_pool_classes || warm_scoped_pools)
		for (PoolSlot *s = pool, *s_end = pool + pool_capacity; s < s_end; ++s)
			if (!s->busy && !s->handle)
			{
				destination = s;
				break;
			}
	if (!destination)
		for (PoolSlot *s = pool, *s_end = pool + pool_capacity; s < s_end; ++s)
			if (!s->busy)
			{
				destination = s;
				break;
			}
	if (destination)
	{
		PoolSlot* s = destination;
		if (s->handle)
		{
			((HandleFree)config.handle_free)(s->handle);
		}
		memset(s, 0, sizeof(*s));
		s->klass = klass;
		s->bytes = ((uint32_t (*)(void*))config.class_size)(klass);
		if (s->bytes < 16 || s->bytes > 512)
		{
			memset(s, 0, sizeof(*s));

			return ((ObjectNew)config.object_new)(klass);
		}
		s->object = ((ObjectNew)config.object_new)(klass);

		s->handle = ((HandleNew)config.handle_new)(s->object, false);
		if (!s->handle)
		{
			void* result = s->object;
			memset(s, 0, sizeof(*s));
			return result;
		}
		s->busy = true;
		*slot = s;
		return s->object;
	}

	return ((ObjectNew)config.object_new)(klass);
}

API void* AcquireBuffIterator(void* klass)
{
	PoolSlot* slot;
	return acquire(klass, &slot);
}

struct ConsumeScope
{
	ConsumeScope* previous;
	void* object;
	PoolSlot* iterator;
	PoolSlot* boxes[32];
	size_t count;
};

static void beginConsumeScope(ConsumeScope* scope, void* object)
{
	memset(scope, 0, sizeof(*scope));
	scope->previous = current_scope;
	scope->object = object;
	for (PoolSlot* slot = pool; slot < pool + pool_capacity; ++slot)
		if (slot->busy && slot->object == object)
		{
			scope->iterator = slot;
			break;
		}
	current_scope = scope;
}

static void endConsumeScope(ConsumeScope* scope)
{
	// Clear the iterator before releasing boxed enumerators disposed by AddRange.
	if (scope->iterator)
		clearSlot(scope->iterator);
	for (size_t i = 0; i < scope->count; ++i)
		clearSlot(scope->boxes[i]);
	current_scope = scope->previous;
}

API void ConsumeBuffs(void* list, void* iterator, void* info)
{
	ConsumeScope scope;
	beginConsumeScope(&scope, iterator);
	__try
	{
		((void (*)(void*, void*, void*))config.buff_add_range)(list, iterator, info);
	}
	__finally
	{
		endConsumeScope(&scope);
	}
}

static void* zoned_list_item_original;
static void* zoned_null_exception;

API void ConfigureZonedRun(void* original, void* raise_null)
{
	zoned_list_item_original = original;
	zoned_null_exception = raise_null;
}

API void* ZonedRunItemReuse(void* zone, int32_t index, void* info, void* item)
{
	if (FIELD(volatile uint64_t, zoned_list_item_original, 0) == 0x83485708245c8948ull)
		return item;
	void* list = FIELD(void*, zone, 0xa8);
	if (!list)
	{
		((void (*)(void))zoned_null_exception)();
		abort();
	}
	void* klass = FIELD(void*, info, 0x20);
	void* context = FIELD(void*, klass, 0xc0);
	return ((void* (*)(void*, int32_t, void*))zoned_list_item_original)(list, index, FIELD(void*, context, 0x70));
}

typedef struct CollisionTraversalConfig
{
	void* original;
	void* initialized;
	void* cell_class_slot;
	void* bloon_class;
	void* collidable_interface;
	void* common_interface;
	void* radius_getter;
	void* transform_getter;
	void* empty_groups_getter;
	void* write_barrier;
} CollisionTraversalConfig;

static CollisionTraversalConfig collision_traversal;
static __declspec(thread) bool traversal_owner = false;
API void ConfigureCollisionTraversal(const CollisionTraversalConfig* c)
{
	collision_traversal = *c;
	traversal_owner = true;
}

static void* traversalBinding(void* klass, void* iface, uint16_t slot)
{
	void* table = FIELD(void*, klass, 0xb0);
	uint16_t count = FIELD(uint16_t, klass, 0x12e);
	if (!table)
		return NULL;
	for (uint16_t i = 0; i < count; ++i)
	{
		void* entry = (char*)table + (size_t)i * 16;
		if (FIELD(void*, entry, 0) == iface)
		{
			int32_t offset = FIELD(int32_t, entry, 8);
			if (offset < 0 || offset > 65535 - slot)
				return NULL;
			return (char*)klass + CLASS_VTABLE_OFFSET + (size_t)(offset + slot) * VIRTUAL_METHOD_SIZE;
		}
	}
	return NULL;
}

static bool traversalPureGettersLive(void)
{
	if (!collision_traversal.radius_getter || !collision_traversal.transform_getter ||
	    !collision_traversal.empty_groups_getter)
		return false;
	// A detour can change the getter without changing its vtable slot.
	return FIELD(volatile uint64_t, collision_traversal.radius_getter, 0) == 0x0000015081100ff3ull &&
	       FIELD(volatile uint8_t, collision_traversal.radius_getter, 8) == 0xc3 &&
	       FIELD(volatile uint64_t, collision_traversal.transform_getter, 0) == 0xc3000000a0818b48ull &&
	       FIELD(volatile uint16_t, collision_traversal.empty_groups_getter, 0) == 0xc033 &&
	       FIELD(volatile uint8_t, collision_traversal.empty_groups_getter, 2) == 0xc3;
}

static uint8_t collisionTraversalOriginal(void* state, void* info)
{
	return ((uint8_t (*)(void*, void*))collision_traversal.original)(state, info);
}

API uint8_t CollisionTraversalNext(void* state, void* info)
{
	if (!traversal_owner || !state || !collision_traversal.initialized ||
	    !FIELD(uint8_t, collision_traversal.initialized, 0))
		return collisionTraversalOriginal(state, info);
	void* grid = FIELD(void*, state, 0x10);
	if (!grid)
		return collisionTraversalOriginal(state, info);
	if (FIELD(uint8_t, grid, 0x34))
	{
		return 0;
	}
	if (!traversalPureGettersLive())
		return collisionTraversalOriginal(state, info);
	void* cell_class =
	    collision_traversal.cell_class_slot ? FIELD(void*, collision_traversal.cell_class_slot, 0) : NULL;
	if (!cell_class || !FIELD(uint32_t, cell_class, 0xe4))
		return collisionTraversalOriginal(state, info);
	void* statics = FIELD(void*, cell_class, 0xb8);
	if (!statics || !collision_traversal.bloon_class)
		return collisionTraversalOriginal(state, info);
	void* radius_pair = traversalBinding(collision_traversal.bloon_class, collision_traversal.collidable_interface, 0);
	void* transform_pair = traversalBinding(collision_traversal.bloon_class, collision_traversal.common_interface, 1);
	void* groups_pair = traversalBinding(collision_traversal.bloon_class, collision_traversal.collidable_interface, 5);
	if (!radius_pair || !transform_pair || !groups_pair)
		return collisionTraversalOriginal(state, info);
	void* neighbors = FIELD(void*, grid, 0x28);
	void* buckets = FIELD(void*, grid, 0x18);
	int32_t i = FIELD(int32_t, state, 0x2c), j = FIELD(int32_t, state, 0x30), end = FIELD(int32_t, state, 0x48);
	if (!neighbors || !buckets || i < 0 || j < 0 || end < 0 ||
	    (uint64_t)end * 2 > FIELD(uint64_t, neighbors, ARRAY_LENGTH_OFFSET))
		return collisionTraversalOriginal(state, info);
	// Leave indices unchanged on failure so the fallback can repeat the reads.
	for (; i < end; ++i)
	{
		size_t neighbor_at = ARRAY_DATA_OFFSET + (size_t)i * 8;
		int32_t ox = FIELD(int32_t, neighbors, neighbor_at), oy = FIELD(int32_t, neighbors, neighbor_at + 4);
		int32_t gx = wrappingAddInt32(FIELD(int32_t, state, 0x34), ox),
		        gy = wrappingAddInt32(FIELD(int32_t, state, 0x38), oy);
		int32_t width = FIELD(int32_t, grid, 0x10), height = FIELD(int32_t, grid, 0x14);
		if (gx < 0 || gx >= width || gy < 0 || gy >= height)
			continue;
		int32_t bucket_index = wrappingAddInt32((int32_t)((uint32_t)gy * (uint32_t)width), gx);
		if (bucket_index < 0 || (uint64_t)bucket_index >= FIELD(uint64_t, buckets, ARRAY_LENGTH_OFFSET))
			return collisionTraversalOriginal(state, info);
		void* bucket = FIELD(void*, buckets, ARRAY_DATA_OFFSET + (size_t)bucket_index * 8);
		if (!bucket)
			return collisionTraversalOriginal(state, info);
		int32_t occupied_span = FIELD(int32_t, bucket, 0x1c);
		if (occupied_span <= 0)
			continue;
		int32_t span = (int32_t)((uint32_t)(ox > oy ? ox : oy) - (uint32_t)(FIELD(int32_t, state, 0x3c)));
		if ((float)span > 0 && occupied_span < span)
			continue;
		bool contained = i == 0;
		if (!contained)
		{
			float half = FIELD(float, statics, 0), x = FIELD(float, state, 0x18), y = FIELD(float, state, 0x1c),
			      radius = FIELD(float, state, 0x20);
			float bx = FIELD(float, bucket, 0x10), by = FIELD(float, bucket, 0x14);
			contained = scalarSub(bx, half) > scalarSub(x, radius) && scalarAdd(x, radius) > scalarAdd(bx, half) &&
			            scalarSub(by, half) > scalarSub(y, radius) && scalarAdd(y, radius) > scalarAdd(by, half);
		}
		void* list = FIELD(void*, bucket, 0x20);
		if (!list)
			return collisionTraversalOriginal(state, info);
		int32_t count = FIELD(int32_t, list, 0x18);
		if (j >= count)
		{
			j = 0;
			continue;
		}
		void* items = FIELD(void*, list, 0x10);
		if (!items || count < 0 || (uint64_t)count > FIELD(uint64_t, items, ARRAY_LENGTH_OFFSET))
			return collisionTraversalOriginal(state, info);
		for (; j < count; ++j)
		{
			void* bloon = FIELD(void*, items, ARRAY_DATA_OFFSET + (size_t)j * 8);
			if (!bloon || FIELD(void*, bloon, 0) != collision_traversal.bloon_class ||
			    FIELD(void*, radius_pair, 0) != collision_traversal.radius_getter ||
			    FIELD(void*, transform_pair, 0) != collision_traversal.transform_getter ||
			    FIELD(void*, groups_pair, 0) != collision_traversal.empty_groups_getter)
				return collisionTraversalOriginal(state, info);

			// Unordered comparisons select the override radius too.
			float override_radius = FIELD(float, state, 0x28);
			float radius = override_radius < 0 ? FIELD(float, bloon, 0x150) : override_radius;
			if (!contained)
			{
				void* transform = FIELD(void*, bloon, 0xa0);
				if (!transform)
					return collisionTraversalOriginal(state, info);
				void* position = FIELD(void*, transform, 0x58);
				if (!position)
					return collisionTraversalOriginal(state, info);
				float dx = scalarSub(FIELD(float, position, 0x10), FIELD(float, state, 0x40));
				float dy = scalarSub(FIELD(float, position, 0x14), FIELD(float, state, 0x44));
				float distance = scalarAdd(scalarMultiply(dx, dx), scalarMultiply(dy, dy));
				float combined = scalarAdd(radius, FIELD(float, state, 0x20));
				// The original rejects ordered greater-than only; NaNs pass.
				if (distance > scalarMultiply(combined, combined))
					continue;
			}
			if (!collision_traversal.write_barrier)
				return collisionTraversalOriginal(state, info);
			FIELD(void*, state, 0x50) = bloon;
			((void (*)(void**, void*))collision_traversal.write_barrier)(&FIELD(void*, state, 0x50), bloon);
			FIELD(int32_t, state, 0x30) = wrappingAddInt32(j, 1);
			FIELD(int32_t, state, 0x2c) = i;

			return 1;
		}
		j = 0;
	}
	FIELD(int32_t, state, 0x2c) = 0;
	FIELD(int32_t, state, 0x30) = j;

	return 0;
}

typedef struct WeaponRateConfig
{
	void* modf_fn;
	void* floor_fn;
	void* ceil_fn;
	void* round_fn;
	void* metadata_init;
	void* class_init;
	void* round_flag;
	void* round_class_slot;
	float multiplier, threshold, half, one;
	double positive_half, negative_half;
} WeaponRateConfig;

static WeaponRateConfig weapon_rate;
static __declspec(thread) bool weapon_rate_owner = false;

typedef struct WeaponRateSlot
{
	uint32_t bits, csr, result_csr;
	int32_t frames;
	bool valid;
} WeaponRateSlot;

static __declspec(thread) WeaponRateSlot weapon_rate_slots[256];
API void ConfigureWeaponRateReuse(const WeaponRateConfig* c)
{
	weapon_rate = *c;
	weapon_rate_owner = true;
	memset(weapon_rate_slots, 0, sizeof(weapon_rate_slots));
}

static int32_t weaponComputeFrames(float rate)
{
	float product = scalarMultiply(rate, weapon_rate.multiplier);
	int32_t result;
	if (weapon_rate.threshold >= rate)
	{
		if (!FIELD(uint8_t, weapon_rate.round_flag, 0))
		{
			((void (*)(void*))weapon_rate.metadata_init)(weapon_rate.round_class_slot);
			FIELD(uint8_t, weapon_rate.round_flag, 0) = 1;
		}
		void* klass = FIELD(void*, weapon_rate.round_class_slot, 0);
		if (!FIELD(uint32_t, klass, 0xe4))
			((void (*)(void*))weapon_rate.class_init)(klass);
		double rounded = ((double (*)(double))weapon_rate.round_fn)((double)product);
		result = _mm_cvttsd_si32(_mm_set_sd(rounded));
	}
	else
	{
		double whole;
		double fraction = ((double (*)(double, double*))weapon_rate.modf_fn)((double)product, &whole);
		if (product >= 0)
		{
			if (fraction == weapon_rate.positive_half)
			{
				int64_t integral = _mm_cvttsd_si64(_mm_set_sd(whole));
				float integer = _mm_cvtss_f32(_mm_cvtsd_ss(_mm_setzero_ps(), _mm_set_sd(whole)));
				if (integral & 1)
					integer = scalarAdd(integer, weapon_rate.one);
				result = _mm_cvttss_si32(_mm_set_ss(integer));
			}
			else
			{
				float rounded = ((float (*)(float))weapon_rate.floor_fn)(scalarAdd(product, weapon_rate.half));
				result = _mm_cvttss_si32(_mm_set_ss(rounded));
			}
		}
		else
		{
			if (fraction == weapon_rate.negative_half)
			{
				int64_t integral = _mm_cvttsd_si64(_mm_set_sd(whole));
				float integer = _mm_cvtss_f32(_mm_cvtsd_ss(_mm_setzero_ps(), _mm_set_sd(whole)));
				if (integral & 1)
					integer = scalarSub(integer, weapon_rate.one);
				result = _mm_cvttss_si32(_mm_set_ss(integer));
			}
			else
			{
				float rounded = ((float (*)(float))weapon_rate.ceil_fn)(scalarSub(product, weapon_rate.half));
				result = _mm_cvttss_si32(_mm_set_ss(rounded));
			}
		}
	}
	return result > 1 ? result : 1;
}

API void WeaponRateFramesReuse(void* weapon, float rate, void* info)
{
	(void)info;
	uint32_t bits;
	memcpy(&bits, &rate, 4);
	uint32_t csr = _mm_getcsr();
	void* round_class = FIELD(void*, weapon_rate.round_class_slot, 0);
	uint32_t magnitude = bits & 0x7fffffff;
	// Nonfinite CRT calls can change flags beyond MXCSR.
	bool numeric = magnitude == 0 || (magnitude >= 0x00800000 && magnitude < 0x7f800000);
	bool ready = weapon_rate_owner && (csr & 0x1f80) == 0x1f80 && numeric &&
	             (rate > weapon_rate.threshold ||
	              (FIELD(uint8_t, weapon_rate.round_flag, 0) && round_class && FIELD(uint32_t, round_class, 0xe4)));
	size_t index = ((bits * 2654435761u) ^ (csr * 2246822519u)) >> 24;
	WeaponRateSlot* slot = &weapon_rate_slots[index];
	if (ready && slot->valid && slot->bits == bits && slot->csr == csr)
	{
		FIELD(int32_t, weapon, 0xa0) = slot->frames;
		_mm_setcsr(slot->result_csr);

		return;
	}
	int32_t frames = weaponComputeFrames(rate);
	uint32_t result_csr = _mm_getcsr();
	FIELD(int32_t, weapon, 0xa0) = frames;

	if (ready)
		slot->bits = bits;
	slot->csr = csr;
	slot->result_csr = result_csr;
	slot->frames = frames;
	slot->valid = true;
}

typedef struct BuffCountConfig
{
	void* get_enumerator;
	void* move_next;
	void* get_info_slot;
	void* move_info_slot;
} BuffCountConfig;

static BuffCountConfig buff_count;
static __declspec(thread) bool buff_count_owner = false;
API void ConfigureBuffCountBatch(const BuffCountConfig* c)
{
	buff_count = *c;
	buff_count_owner = true;
}

static uint8_t nextBuffFlag(void* enumerator)
{
	return ((uint8_t (*)(void*, void*))buff_count.move_next)(enumerator, FIELD(void*, buff_count.move_info_slot, 0));
}

static int32_t countBuffFlagsOriginal(void* enumerator)
{
	uint32_t count = 0;
	while (nextBuffFlag(enumerator))
		count += FIELD(uint8_t, enumerator, 0x34) != 0;
	return (int32_t)count;
}

API int32_t CountBuffFlagsBatch(void* list)
{
	// The original constructor captures the version and initializes generic metadata.
	__declspec(align(16)) unsigned char enumerator[56] = {0};
	((void* (*)(void*, void*, void*))buff_count.get_enumerator)(enumerator, list,
	                                                            FIELD(void*, buff_count.get_info_slot, 0));

	if (!buff_count_owner || !list)
		return countBuffFlagsOriginal(enumerator);
	void* array = FIELD(void*, list, 0x10);
	int32_t count = FIELD(int32_t, list, 0x18), version = FIELD(int32_t, enumerator, 0xc);
	if (count < 0 || (!array && count != 0) || (array && (uint64_t)count > FIELD(uint64_t, array, ARRAY_LENGTH_OFFSET)))
		return countBuffFlagsOriginal(enumerator);
	uint32_t result = 0;
	int32_t i = 0;
	for (; i <= count - 8; i += 8)
	{
		void* p = (char*)array + 0x44 + (size_t)i * 40;
		result += (FIELD(uint8_t, p, 0) != 0) + (FIELD(uint8_t, p, 40) != 0) + (FIELD(uint8_t, p, 80) != 0) +
		          (FIELD(uint8_t, p, 120) != 0) + (FIELD(uint8_t, p, 160) != 0) + (FIELD(uint8_t, p, 200) != 0) +
		          (FIELD(uint8_t, p, 240) != 0) + (FIELD(uint8_t, p, 280) != 0);
	}
	for (; i < count; ++i)
		result += FIELD(uint8_t, array, 0x44 + (size_t)i * 40) != 0;
	if (FIELD(volatile int32_t, list, 0x1c) != version)
		return countBuffFlagsOriginal(enumerator);
	FIELD(int32_t, enumerator, 8) = count;
	// The terminal MoveNext preserves version exceptions from the captured snapshot.
	if (nextBuffFlag(enumerator))
		return countBuffFlagsOriginal(enumerator);

	return (int32_t)result;
}

typedef struct ModelScaleConfig
{
	void* original;
	uint8_t* metadata_initialized;
	void** vector_class_slot;
} ModelScaleConfig;

static ModelScaleConfig model_scale;

API void ConfigureModelScale(const ModelScaleConfig* c)
{
	model_scale = *c;
}

API void* ModelScaleCalculation(void* output, void* behavior, void* info)
{
	void* klass = *model_scale.vector_class_slot;
	if (!*model_scale.metadata_initialized || !klass || !FIELD(int32_t, klass, 0xE4) || !behavior || !output)
		return ((void* (*)(void*, void*, void*))model_scale.original)(output, behavior, info);
	void* model = FIELD(void*, behavior, 0x70);
	void* scale = model ? FIELD(void*, model, 0x68) : NULL;
	if (!scale)
		return ((void* (*)(void*, void*, void*))model_scale.original)(output, behavior, info);
	float x = scalarMultiply(FIELD(float, scale, 0x10), FIELD(float, behavior, 0x84));
	float y = scalarMultiply(FIELD(float, scale, 0x14), FIELD(float, behavior, 0x88));
	float z = scalarMultiply(FIELD(float, scale, 0x18), FIELD(float, behavior, 0x8C));
	FIELD(float, output, 0) = x;
	FIELD(float, output, 4) = y;
	FIELD(float, output, 8) = z;
	return output;
}

API void* ModelScaleCoordinates(void* output, void* input)
{
	// This call site uses Flip(false): swap Y and Z without changing their bits.
	uint32_t x = FIELD(uint32_t, input, 0);
	uint32_t y = FIELD(uint32_t, input, 4);
	uint32_t z = FIELD(uint32_t, input, 8);
	FIELD(uint32_t, output, 0) = x;
	FIELD(uint32_t, output, 4) = z;
	FIELD(uint32_t, output, 8) = y;
	return output;
}

static void* current_projection_original;
static void* current_projection_value;
static uint8_t* current_projection_boxed_flag;
static uint8_t* current_projection_value_flag;
static void** current_projection_class_slot;
API void ConfigureCurrentModelProjection(void* original, void* value, uint8_t* boxed_flag, uint8_t* value_flag,
                                         void** class_slot)
{
	current_projection_original = original;
	current_projection_value = value;
	current_projection_boxed_flag = boxed_flag;
	current_projection_value_flag = value_flag;
	current_projection_class_slot = class_slot;
}

API void* CurrentModelProjection(void* output, void* boxed, uint8_t perspective, float angle, void* info)
{
	void* klass = *current_projection_class_slot;
	if (output && boxed && *current_projection_boxed_flag && *current_projection_value_flag && klass &&
	    FIELD(int32_t, klass, 0xe4))
		return ((void* (*)(void*, void*, uint8_t, float, void*))current_projection_value)(output, (char*)boxed + 0x10,
		                                                                                  perspective, angle, NULL);
	return ((void* (*)(void*, void*, uint8_t, float, void*))current_projection_original)(output, boxed, perspective,
	                                                                                     angle, info);
}

typedef struct ModelDisplayConfig
{
	void* boxed_method;
	void* boxed_initialized;
	void* vector_initialized;
	void* class_slot;
} ModelDisplayConfig;

static ModelDisplayConfig model_display;
API void ConfigureModelDisplay(const ModelDisplayConfig* c)
{
	model_display = *c;
}

typedef void* (*DisplayProjection)(void*, void*, uint8_t, float, void*);
API void* ModelDisplayFast(void* output, void* boxed, uint8_t perspective, float angle, void* info)
{
	const ModelDisplayConfig* c = &model_display;
	void* klass = c->class_slot ? FIELD(void*, c->class_slot, 0) : NULL;
	bool ready = configured_thread && boxed && output && c->boxed_initialized && c->vector_initialized &&
	             FIELD(uint8_t, c->boxed_initialized, 0) && FIELD(uint8_t, c->vector_initialized, 0) && klass &&
	             FIELD(int32_t, klass, 0xe4) != 0;
	void* statics = ready && perspective ? FIELD(void*, klass, 0xb8) : NULL;
	uint32_t tangent_bits = statics ? FIELD(uint32_t, statics, 0x60) : 0;
	if (!ready || (perspective && (!statics || (tangent_bits & 0x7fffffff) > 0x7f800000)))
	{
		return ((DisplayProjection)c->boxed_method)(output, boxed, perspective, angle, info);
	}
	// Load inputs before writing output so overlapping buffers remain valid.
	// Scalar SSE preserves operand order, rounding and NaN payloads.
	__m128 x = _mm_load_ss((float*)boxed + 4);
	__m128 height = _mm_load_ss((float*)boxed + 6);
	__m128 neg_y = _mm_xor_ps(_mm_load_ss((float*)boxed + 5), _mm_castsi128_ps(_mm_set_epi32(0, 0, 0, INT32_MIN)));
	if (perspective)
	{
		__m128 adjustment = _mm_setzero_ps();
		if ((tangent_bits & 0x7fffffff) != 0)
			adjustment = _mm_div_ss(height, _mm_castsi128_ps(_mm_cvtsi32_si128((int32_t)tangent_bits)));
		neg_y = _mm_sub_ss(neg_y, adjustment);
	}
	_mm_store_ss((float*)output, x);
	_mm_store_ss((float*)output + 1, height);
	_mm_store_ss((float*)output + 2, neg_y);

	return output;
}

typedef struct ModelRotationConfig
{
	void* original;
	void* initialized;
	void* scene_class_slot;
} ModelRotationConfig;

static ModelRotationConfig model_rotation;
static __declspec(thread) bool model_rotation_owner = false;

typedef struct ModelRotationSlot
{
	uint32_t angle, range, csr, result_csr;
	int32_t steps, index;
	bool valid;
} ModelRotationSlot;

static __declspec(thread) ModelRotationSlot model_rotation_slots[256];
API void ConfigureModelRotationReuse(const ModelRotationConfig* c)
{
	model_rotation = *c;
	model_rotation_owner = true;
	memset(model_rotation_slots, 0, sizeof(model_rotation_slots));
}

static bool rotationNormalOrZero(uint32_t bits)
{
	uint32_t a = bits & 0x7fffffff;
	return a == 0 || (a >= 0x00800000 && a < 0x7f800000);
}

typedef void* (*RotationLookup)(void*, float, uint8_t, void*);

static void* modelRotationOriginal(void* output, float angle, uint8_t is2d, void* info)
{
	return ((RotationLookup)model_rotation.original)(output, angle, is2d, info);
}

API void* ModelRotationLookupReuse(void* output, float angle, uint8_t is2d, void* info)
{
	const float input_angle = angle;

	if (!model_rotation_owner || !output || !FIELD(uint8_t, model_rotation.initialized, 0))
		return modelRotationOriginal(output, input_angle, is2d, info);
	void* klass = FIELD(void*, model_rotation.scene_class_slot, 0);
	if (!klass || !FIELD(uint32_t, klass, 0xe4))
		return modelRotationOriginal(output, input_angle, is2d, info);
	void* statics = FIELD(void*, klass, 0xb8);
	if (!statics || !FIELD(uint8_t, statics, 4))
		return modelRotationOriginal(output, input_angle, is2d, info);
	float range = FIELD(float, statics, 0);
	int32_t steps = FIELD(int32_t, statics, 8);
	uint32_t range_bits, angle_bits;
	memcpy(&range_bits, &range, 4);
	memcpy(&angle_bits, &angle, 4);
	uint32_t csr = _mm_getcsr();
	if ((csr & 0x1f80) != 0x1f80 || !rotationNormalOrZero(angle_bits) || range_bits < 0x00800000 ||
	    range_bits >= 0x7f000000 || steps <= 0)
		return modelRotationOriginal(output, input_angle, is2d, info);
	// Check eligibility with integer bits to avoid changing floating-point flags.
	uint32_t absolute = angle_bits & 0x7fffffff;
	if (absolute > range_bits)
		return modelRotationOriginal(output, input_angle, is2d, info);
	void* normal = FIELD(void*, statics, 0x10);
	void* selected = is2d ? FIELD(void*, statics, 0x18) : normal;
	if (!normal || !selected || FIELD(uint64_t, normal, ARRAY_LENGTH_OFFSET) > 0x7fffffff ||
	    FIELD(uint64_t, selected, ARRAY_LENGTH_OFFSET) > 0x7fffffff)
		return modelRotationOriginal(output, input_angle, is2d, info);
	ModelRotationSlot* slot =
	    &model_rotation_slots[((angle_bits * 2654435761u) ^ (range_bits * 2246822519u) ^ (uint32_t)steps ^ csr) >> 24];
	int32_t index;
	if (slot->valid && slot->angle == angle_bits && slot->range == range_bits && slot->steps == steps &&
	    slot->csr == csr)
	{
		index = slot->index;
		_mm_setcsr(slot->result_csr);
	}
	else
	{
		// Finite angles in this range need at most one normalization add.
		if (angle < 0)
			angle = scalarAdd(angle, range);
		float divided = _mm_cvtss_f32(_mm_div_ss(_mm_set_ss(angle), _mm_set_ss(range)));
		float count_float = _mm_cvtss_f32(_mm_cvtsi32_ss(_mm_setzero_ps(), steps));
		float scaled = scalarMultiply(divided, count_float);
		index = _mm_cvttss_si32(_mm_set_ss(scaled));
		*slot = (ModelRotationSlot){angle_bits, range_bits, csr, _mm_getcsr(), steps, index, true};
	}
	if (index < 0 || (uint64_t)index >= FIELD(uint64_t, normal, ARRAY_LENGTH_OFFSET))
		index = 0;
	if ((uint64_t)index >= FIELD(uint64_t, selected, ARRAY_LENGTH_OFFSET))
		return modelRotationOriginal(output, input_angle, is2d, info);
	memcpy(output, (char*)selected + ARRAY_DATA_OFFSET + (size_t)index * 16, 16);
	return output;
}

typedef struct ProjectileTimerConfig
{
	void* original_age;
	void* age_initialized;
	void* common_interface;
	void* raise_null;
} ProjectileTimerConfig;

static ProjectileTimerConfig projectile_timer;
static __declspec(thread) bool projectile_timer_owner = false;

typedef struct TimerBinding
{
	void* klass;
	void* table;
	void* pair;
	void* entry;
	uint16_t count;
	int32_t offset;
} TimerBinding;

static __declspec(thread) TimerBinding timer_binding;
API void ConfigureProjectileTimers(const ProjectileTimerConfig* c)
{
	projectile_timer = *c;
	projectile_timer_owner = true;
	memset(&timer_binding, 0, sizeof(timer_binding));
}

static __forceinline int32_t timerSub(int32_t a, int32_t b)
{
	return (int32_t)((uint32_t)a - (uint32_t)b);
}

static int32_t projectileAgeOriginal(void* behavior, void* info)
{
	return ((int32_t (*)(void*, void*))projectile_timer.original_age)(behavior, info);
}

API int32_t ProjectileOwnerAgeFast(void* behavior, void* info)
{
	if (!projectile_timer_owner || !behavior || !FIELD(uint8_t, projectile_timer.age_initialized, 0))
		return projectileAgeOriginal(behavior, info);
	void* model = FIELD(void*, behavior, 0x68);
	void* root = FIELD(void*, behavior, 0x18);
	if (!model || !root)
		return projectileAgeOriginal(behavior, info);
	if (FIELD(uint8_t, model, 0x34))
	{
		void* counter = FIELD(void*, root, 0x38);
		if (!counter)
			return projectileAgeOriginal(behavior, info);

		return timerSub(FIELD(int32_t, counter, 0x10), FIELD(int32_t, behavior, 0x74));
	}
	void* counter = FIELD(void*, root, 0x30);
	void* owner = FIELD(void*, behavior, 0x58);
	if (!counter || !owner)
		return projectileAgeOriginal(behavior, info);
	// Read the counter before invoking the getter; overrides can change it.
	int32_t current = FIELD(int32_t, counter, 0x10);
	void* klass = FIELD(void*, owner, 0);
	if (!klass)
		return projectileAgeOriginal(behavior, info);
	void* table = FIELD(void*, klass, 0xb0);
	uint16_t count = FIELD(uint16_t, klass, 0x12e), slot = 15;
	void* pair;
	if (timer_binding.klass == klass && timer_binding.table == table && timer_binding.count == count &&
	    timer_binding.entry && FIELD(void*, timer_binding.entry, 0) == projectile_timer.common_interface &&
	    FIELD(int32_t, timer_binding.entry, 8) == timer_binding.offset)
	{
		pair = timer_binding.pair;
	}
	else
	{
		pair = traversalBinding(klass, projectile_timer.common_interface, slot);
		if (!pair)
			return projectileAgeOriginal(behavior, info);
		void* entry = NULL;
		for (uint16_t i = 0; i < count; ++i)
		{
			void* p = (char*)table + (size_t)i * 16;
			if (FIELD(void*, p, 0) == projectile_timer.common_interface)
			{
				entry = p;
				break;
			}
		}
		if (!entry)
			return projectileAgeOriginal(behavior, info);
		timer_binding = (TimerBinding){klass, table, pair, entry, count, FIELD(int32_t, entry, 8)};
	}
	void* getter = FIELD(void*, pair, 0);
	if (!getter)
		return projectileAgeOriginal(behavior, info);
	int32_t owner_age = ((int32_t (*)(void*, void*))getter)(owner, FIELD(void*, pair, 8));

	return timerSub(current, owner_age);
}

static void raiseProjectileNullReference(void)
{
	((void (*)(void))projectile_timer.raise_null)();
}

API void ClearProjectileHistoryScheduled(void* behavior, int32_t frame, void* info)
{
	(void)info;
	void* model = FIELD(void*, behavior, 0x68);

	if (!model)
	{
		raiseProjectileNullReference();
		return;
	}
	int32_t delay = timerSub(FIELD(int32_t, model, 0x34), FIELD(int32_t, behavior, 0x74));
	if (delay < 0)
		delay = 0;
	float frame_float = _mm_cvtss_f32(_mm_cvtsi32_ss(_mm_setzero_ps(), frame));
	float delay_float = _mm_cvtss_f32(_mm_cvtsi32_ss(_mm_setzero_ps(), delay));
	float threshold = scalarAdd(delay_float, FIELD(float, behavior, 0x70));
	// The original also waits on unordered comparisons, including NaN.
	if (!(frame_float >= threshold))
		return;
	void* projectile = FIELD(void*, behavior, 0x60);
	if (!projectile)
	{
		raiseProjectileNullReference();
		return;
	}
	void* history = FIELD(void*, projectile, 0x160);
	if (!history)
	{
		raiseProjectileNullReference();
		return;
	}
	FIELD(int32_t, history, 0x1c) = wrappingAddInt32(FIELD(int32_t, history, 0x1c), 1);
	FIELD(int32_t, history, 0x18) = 0;
	FIELD(float, behavior, 0x70) = frame_float;
}

static bool scanIds(const uint32_t* l, int ln, const uint32_t* h, int hn)
{
	for (int i = 0; i < ln; ++i)
	{
		int j = 0;
		if (hn >= 8)
		{
			const __m128i key = _mm_set1_epi32((int32_t)l[i]);
			for (; j <= hn - 4; j += 4)
				if (_mm_movemask_epi8(_mm_cmpeq_epi32(_mm_loadu_si128((const __m128i*)(h + j)), key)))
					return true;
		}
		for (; j < hn; ++j)
			if (l[i] == h[j])
				return true;
	}
	return false;
}

static volatile LONG indexed_enabled;

typedef struct IdIndex
{
	uint32_t keys[16384], stamps[16384], epoch;
} IdIndex;

static __declspec(thread) IdIndex id_index;
API void SetIndexedLineage(uint8_t enabled)
{
	InterlockedExchange(&indexed_enabled, enabled != 0);
}

static bool tryIndexed(const uint32_t* lineage, int32_t ln, const uint32_t* history, int32_t hn, bool* found)
{
	if (ln < 32 || hn < 256 || hn > 8192 || (uint64_t)ln * (uint64_t)hn < 65536)
		return false;
	if (scanIds(lineage, ln < 2 ? ln : 2, history, hn < 16 ? hn : 16))
	{
		*found = true;
		return true;
	}
	IdIndex* index = &id_index;
	if (++index->epoch == 0)
	{
		memset(index->stamps, 0, sizeof(index->stamps));
		index->epoch = 1;
	}
	for (int32_t i = 0; i < hn; ++i)
	{
		uint32_t key = history[i], slot = (key * 2654435761u) & 16383u, probe = 0;
		while (index->stamps[slot] == index->epoch && index->keys[slot] != key)
		{
			if (++probe == 32)
				return false;
			slot = (slot + 1) & 16383u;
		}
		index->stamps[slot] = index->epoch;
		index->keys[slot] = key;
	}
	for (int32_t i = 0; i < ln; ++i)
	{
		uint32_t key = lineage[i], slot = (key * 2654435761u) & 16383u, probe = 0;
		while (index->stamps[slot] == index->epoch)
		{
			if (index->keys[slot] == key)
			{
				*found = true;
				return true;
			}
			if (++probe == 32)
				return false;
			slot = (slot + 1) & 16383u;
		}
	}
	*found = false;
	return true;
}

typedef uint8_t (*LineageFn)(void*, void*, void*);
static LineageFn lineage_original = NULL;
API void SetLineageOriginal(void* p)
{
	lineage_original = ((LineageFn)p);
}

static bool listView(void* list, const uint32_t** data, int32_t* count, int32_t* version)
{
	if (!list)
		return false;
	void* array = FIELD(void*, list, 0x10);
	*count = FIELD(int32_t, list, 0x18);
	*version = FIELD(volatile int32_t, list, 0x1c);
	if (!array || *count < 0 || (uint64_t)*count > FIELD(uint64_t, array, ARRAY_LENGTH_OFFSET))
		return false;
	*data = (uint32_t*)((char*)array + ARRAY_DATA_OFFSET);
	return true;
}

API uint8_t LineageScan(void* projectile, void* bloon, void* info)
{
	if (!projectile || !bloon)
		return lineage_original(projectile, bloon, info);
	void* lineage = FIELD(void*, bloon, 0x180);
	void* history = FIELD(void*, projectile, 0x160);
	const uint32_t *l, *h;
	int32_t ln, hn, lv, hv;
	if (!listView(lineage, &l, &ln, &lv) || !listView(history, &h, &hn, &hv))
		return lineage_original(projectile, bloon, info);
	bool found = false;
	if (!(configured_thread && InterlockedCompareExchange(&indexed_enabled, 0, 0) && tryIndexed(l, ln, h, hn, &found)))
		found = scanIds(l, ln, h, hn);
	if (FIELD(volatile int32_t, lineage, 0x1c) != lv || FIELD(volatile int32_t, history, 0x1c) != hv)
		return lineage_original(projectile, bloon, info);
	return found ? 1 : 0;
}

typedef struct ComparerSlot
{
	void* attack;
	void* selector;
	GcHandle handle;
} ComparerSlot;

static __declspec(thread) ComparerSlot comparers[1024];
typedef void (*ComparerConstructor)(void*, void*, void*, void*);

static void* allocatePathComparer(void* attack, void* selector)
{
	void* result = ((ObjectNew)config.object_new)(config.comparer_class);
	((ComparerConstructor)config.comparer_ctor)(result, attack, selector, NULL);
	return result;
}

API void* CachedComparer(void* attack, void* selector)
{
	if (!configured_thread)
		return allocatePathComparer(attack, selector);
	const size_t hash = (((uintptr_t)attack >> 4) ^ ((uintptr_t)selector >> 3)) & 1023;
	ComparerSlot* slot = &comparers[hash];
	if (slot->handle)
	{
		void* candidate = ((HandleTarget)config.handle_target)(slot->handle);
		if (candidate && slot->attack == attack && slot->selector == selector &&
		    FIELD(void*, candidate, 0x20) == attack && FIELD(void*, candidate, 0x28) == selector)
			return candidate;
		((HandleFree)config.handle_free)(slot->handle);
		memset(slot, 0, sizeof(*slot));
	}
	void* result = allocatePathComparer(attack, selector);
	slot->attack = attack;
	slot->selector = selector;
	slot->handle = ((HandleNew)config.weak_new)(result, false);
	return result;
}

typedef struct CurrentBuffAllocationConfig
{
	void* other_class;
	void* discount_class;
	void* add_range;
} CurrentBuffAllocationConfig;

static CurrentBuffAllocationConfig current_buff_allocation;
API void ConfigureCurrentBuffAllocation(const CurrentBuffAllocationConfig* c)
{
	current_buff_allocation = *c;
	config.buff_add_range = c->add_range;
	warm_scoped_pools = true;
}

API void* AcquireCurrentBuffIterator(void* klass)
{
	if (klass != current_buff_allocation.other_class && klass != current_buff_allocation.discount_class)
		return ((ObjectNew)config.object_new)(klass);
	PoolSlot* slot;
	return acquire(klass, &slot);
}

typedef void (*LockEnumeratorConstructor)(void*, void*, void*, void*, void*);
API void* BuffLockEnumerator(void* lock_list, void* info, void* owner_iterator)
{
	if (!configured_thread || !current_scope || current_scope->object != owner_iterator)
		return ((void* (*)(void*, void*))config.lock_enumerator_original)(lock_list, info);
	++FIELD(int32_t, lock_list, 0x20);
	void* klass = FIELD(void*, info, 0x20);
	void* rgctx = FIELD(void*, klass, 0xc0);
	__declspec(align(16)) unsigned char list_enum[24] = {0}, lock_enum[40] = {0};
	((void (*)(void*, void*, void*))config.list_enumerator)(list_enum, FIELD(void*, lock_list, 0x10),
	                                                        FIELD(void*, rgctx, 0x138));
	((LockEnumeratorConstructor)config.lock_enumerator_ctor)(lock_enum, list_enum, FIELD(void*, lock_list, 0x30),
	                                                         FIELD(void*, lock_list, 0x28), FIELD(void*, rgctx, 0x150));
	void* box_class = FIELD(void*, rgctx, 0x148);
	PoolSlot* slot;
	void* box = acquire(box_class, &slot);
	if (slot && slot->bytes != 56)
	{
		clearSlot(slot);
		slot = NULL;
		box = ((ObjectNew)config.object_new)(box_class);
	}
	memcpy((char*)box + 24, lock_enum + 8, 8);
	const uint32_t reference_offsets[] = {0, 16, 24, 32};
	for (size_t i = 0; i < sizeof(reference_offsets) / sizeof(reference_offsets[0]); ++i)
		storeReference(box, (void**)((char*)box + 16 + reference_offsets[i]),
		               FIELD(void*, lock_enum, reference_offsets[i]));
	if (slot && current_scope && current_scope->count < 32)
		current_scope->boxes[current_scope->count++] = slot;
	return box;
}

typedef struct ExtendedConfig
{
	void* find_zones;
	void* buff_get_enumerator;
	void* buff_move_next;
	void* buff_get_info_slot;
	void* buff_move_info_slot;
} ExtendedConfig;

static ExtendedConfig extended;
API void ConfigureExtended(const ExtendedConfig* c)
{
	extended = *c;
}

static int32_t countOriginal(void* list)
{
	__declspec(align(16)) unsigned char enumerator[56] = {0};
	((void (*)(void*, void*, void*))extended.buff_get_enumerator)(enumerator, list,
	                                                              FIELD(void*, extended.buff_get_info_slot, 0));
	uint32_t count = 0;
	while (
	    ((uint8_t (*)(void*, void*))extended.buff_move_next)(enumerator, FIELD(void*, extended.buff_move_info_slot, 0)))
		if (FIELD(uint8_t, enumerator, 0x34))
			++count;
	return (int32_t)count;
}

API int32_t CountAchievementBuffs(void* list, void* info)
{
	(void)info;
	if (!list)
		return countOriginal(list);
	void* array = FIELD(void*, list, 0x10);
	int32_t count = FIELD(int32_t, list, 0x18), version = FIELD(volatile int32_t, list, 0x1c);
	if (!array || count < 0 || (uint64_t)count > FIELD(uint64_t, array, ARRAY_LENGTH_OFFSET))
		return countOriginal(list);
	uint32_t result = 0;
	for (int32_t i = 0; i < count; ++i)
		if (FIELD(uint8_t, array, ARRAY_DATA_OFFSET + (size_t)i * 40 + 0x24))
			++result;
	if (FIELD(volatile int32_t, list, 0x1c) != version)
		return countOriginal(list);
	return (int32_t)result;
}

static __declspec(thread) bool zone_acquire_allowed = false;
API void* AcquireZoneIterator(void* klass)
{
	if (!configured_thread || !zone_acquire_allowed || !current_scope || current_scope->count == 32)
		return ((ObjectNew)config.object_new)(klass);
	PoolSlot* slot = NULL;
	void* object = acquire(klass, &slot);
	if (slot)
	{
		current_scope->boxes[current_scope->count++] = slot;
	}
	return object;
}

typedef void* (*DiscountZoneLookup)(void*, int32_t, void*);
API void* ScopedFindDiscountZones(void* tower, int32_t path, void* info)
{
	bool previous = zone_acquire_allowed;
	void* result;
	zone_acquire_allowed = true;
	__try
	{
		result = ((DiscountZoneLookup)extended.find_zones)(tower, path, info);
	}
	__finally
	{
		zone_acquire_allowed = previous;
	}
	return result;
}

static void* collision_position_getter;
static uint8_t* collision_position_initialized;
API void ConfigureCollisionPosition(void* getter, uint8_t* initialized)
{
	collision_position_getter = getter;
	collision_position_initialized = initialized;
}

API void* CollisionPositionFast(void* object)
{
	void* klass = FIELD(void*, object, 0);
	void* method = FIELD(void*, klass, 0x828);
	if (method == collision_position_getter && *collision_position_initialized &&
	    FIELD(volatile uint64_t, method, 0) == 0x3d8020ec83485340ull)
	{
		void* transform = FIELD(void*, object, 0xa0);
		if (transform)
			return FIELD(void*, transform, 0x58);
	}
	return ((void* (*)(void*, void*))method)(object, FIELD(void*, klass, 0x830));
}

static void* iterator_advance_original;
static void* iterator_count_method;
API void ConfigureIteratorAdvance(void* original, void* count)
{
	iterator_advance_original = original;
	iterator_count_method = count;
}

API uint8_t UnfilteredIteratorNext(void* enumerator, void* info)
{
	if (enumerator && info && !FIELD(void*, enumerator, 0x10))
	{
		void* owner = FIELD(void*, enumerator, 0);
		void* klass = FIELD(void*, info, 0x20);
		void* list = owner ? FIELD(void*, owner, 0x10) : NULL;
		if (list && klass && (FIELD(uint8_t, klass, 0x135) & 1) &&
		    FIELD(volatile uint64_t, iterator_count_method, 0) == 0xc318408b10418b48ull)
		{
			int32_t count = FIELD(int32_t, list, 0x18);
			int32_t index = wrappingAddInt32(FIELD(int32_t, enumerator, 8), 1);
			FIELD(int32_t, enumerator, 8) = index;
			return count >= index;
		}
	}
	return ((uint8_t (*)(void*, void*))iterator_advance_original)(enumerator, info);
}

typedef struct DispatchConfig
{
	void* resolve;
	void* current;
} DispatchConfig;

static DispatchConfig dispatch;

typedef struct DispatchSlot
{
	void* klass;
	void* iface;
	void* table;
	uint16_t index;
	int32_t offset;
} DispatchSlot;

static __declspec(thread) DispatchSlot dispatch_cache[512];
API void ConfigureDispatch(const DispatchConfig* c)
{
	dispatch = *c;
}

typedef void* (*InterfaceResolve)(void*, void*, int32_t);

static void* interfaceEntry(void* table, uint16_t index)
{
	return (char*)table + (size_t)index * 16;
}

static void* virtualMethodPair(void* klass, int32_t slot, int32_t offset)
{
	// Match the original wrapping addition and signed offset extension.
	int32_t sum = (int32_t)((uint32_t)offset + (uint32_t)slot);
	return (char*)klass + CLASS_VTABLE_OFFSET + (int64_t)sum * VIRTUAL_METHOD_SIZE;
}

API void* ResolveInterfaceFast(void* object, void* iface, int32_t slot)
{
	void* klass = FIELD(void*, object, 0);
	void* table = FIELD(void*, klass, 0xb0);
	uint16_t count = FIELD(uint16_t, klass, 0x12e);

	DispatchSlot* cached = NULL;
	if (configured_thread && count >= 4)
	{
		size_t key = (((uintptr_t)klass >> 4) ^ ((uintptr_t)iface >> 3)) & 511;
		cached = &dispatch_cache[key];
		if (cached->klass == klass && cached->iface == iface && cached->table == table && cached->index < count &&
		    FIELD(void*, interfaceEntry(table, cached->index), 0) == iface &&
		    FIELD(int32_t, interfaceEntry(table, cached->index), 8) == cached->offset)
		{
			return virtualMethodPair(klass, slot, cached->offset);
		}
	}

	for (uint16_t i = 0; i < count; ++i)
		if (FIELD(void*, interfaceEntry(table, i), 0) == iface)
		{
			int32_t offset = FIELD(int32_t, interfaceEntry(table, i), 8);
			if (cached)
				*cached = (DispatchSlot){klass, iface, table, i, offset};
			return virtualMethodPair(klass, slot, offset);
		}
	return ((InterfaceResolve)dispatch.resolve)(object, iface, slot);
}

API void ConfigureLockCurrent(void* current)
{
	dispatch.current = current;
}

static void* lockCurrentOriginal(void* enumerator, void* info)
{
	return ((void* (*)(void*, void*))dispatch.current)(enumerator, info);
}

static __forceinline void* lockCurrentImpl(void* enumerator, void* info)
{
	int32_t index = FIELD(int32_t, enumerator, 8);
	if (index <= 0)
		return lockCurrentOriginal(enumerator, info);
	void* owner = FIELD(void*, enumerator, 0);
	if (!owner)
		return lockCurrentOriginal(enumerator, info);
	void* list = FIELD(void*, owner, 0x10);
	if (!list || !info)
		return lockCurrentOriginal(enumerator, info);
	void* klass = FIELD(void*, info, 0x20);
	if (!klass || !(FIELD(uint8_t, klass, 0x135) & 1))
		return lockCurrentOriginal(enumerator, info);
	uint32_t offset = (uint32_t)index - 1;
	if (offset >= (uint32_t)(FIELD(int32_t, list, 0x18)))
		return lockCurrentOriginal(enumerator, info);
	void* array = FIELD(void*, list, 0x10);
	if (!array || offset >= FIELD(uint32_t, array, 0x18))
		return lockCurrentOriginal(enumerator, info);

	return FIELD(void*, array, ARRAY_DATA_OFFSET + (size_t)offset * 8);
}

API void* LockCurrentFast(void* enumerator, void* info)
{
	return lockCurrentImpl(enumerator, info);
}

static void clearDispatch(void)
{
	for (DispatchSlot* slot = dispatch_cache; slot < dispatch_cache + 512; ++slot)
		memset(slot, 0, sizeof(*slot));
}

API void* CollisionLockCurrentFast(void* enumerator, void* info)
{
	return lockCurrentImpl(enumerator, info);
}

API void* WeaponLockCurrentFast(void* enumerator, void* info)
{
	return lockCurrentImpl(enumerator, info);
}

API void* ModelLockCurrentFast(void* enumerator, void* info)
{
	return lockCurrentImpl(enumerator, info);
}

API void* WeaponAnimationCurrentFast(void* enumerator, void* info)
{
	return lockCurrentImpl(enumerator, info);
}

API void* CollisionCheckerCurrentFast(void* enumerator, void* info)
{
	return lockCurrentImpl(enumerator, info);
}

API void* CollisionCallbackResolveFast(void* object, void* iface, int32_t slot)
{
	void* result = ResolveInterfaceFast(object, iface, slot);

	return result;
}

typedef struct CandidateDispatchConfig
{
	void* pointer_invoker;
	void* float_invoker;
	void* transform_getter;
	void* radius_getter;
	void* empty_collisions_getter;
	void* bloon_class;
	void* common_iface;
	void* collidable_iface;
} CandidateDispatchConfig;

static CandidateDispatchConfig candidate_dispatch_config;

typedef struct CandidateBinding
{
	void* table;
	uint16_t index;
	int32_t offset;
	bool valid;
} CandidateBinding;

static __declspec(thread) CandidateBinding bloon_bindings[2];
API void ConfigureCandidateDispatch(const CandidateDispatchConfig* c)
{
	candidate_dispatch_config = *c;
	for (CandidateBinding* binding = bloon_bindings; binding < bloon_bindings + 2; ++binding)
		memset(binding, 0, sizeof(*binding));
}

static __forceinline void* candidatePair(uint16_t slot, void* iface, void* object)
{
	void* klass = FIELD(void*, object, 0);
	void* table = FIELD(void*, klass, 0xb0);
	uint16_t count = FIELD(uint16_t, klass, 0x12e);
	if (klass == candidate_dispatch_config.bloon_class &&
	    (iface == candidate_dispatch_config.common_iface || iface == candidate_dispatch_config.collidable_iface))
	{
		CandidateBinding* binding = &bloon_bindings[iface == candidate_dispatch_config.common_iface ? 0 : 1];

		if (binding->valid && binding->table == table && binding->index < count)
		{
			void* entry = (char*)table + (size_t)binding->index * 16;
			if (FIELD(void*, entry, 0) == iface && FIELD(int32_t, entry, 8) == binding->offset)
				return virtualMethodPair(klass, slot, binding->offset);
		}
		for (uint16_t i = 0; i < count; ++i)
		{
			void* entry = (char*)table + (size_t)i * 16;
			if (FIELD(void*, entry, 0) == iface)
			{
				*binding = (CandidateBinding){table, i, FIELD(int32_t, entry, 8), true};
				return virtualMethodPair(klass, slot, binding->offset);
			}
		}
		memset(binding, 0, sizeof(*binding));
		return ((InterfaceResolve)dispatch.resolve)(object, iface, slot);
	}
	for (uint16_t i = 0; i < (count < 4 ? count : 1); ++i)
	{
		void* entry = (char*)table + (size_t)i * 16;
		if (FIELD(void*, entry, 0) == iface)
		{
			int32_t sum = (int32_t)((uint32_t)(FIELD(int32_t, entry, 8)) + slot);
			return (char*)klass + CLASS_VTABLE_OFFSET + (int64_t)sum * VIRTUAL_METHOD_SIZE;
		}
	}
	return ResolveInterfaceFast(object, iface, slot);
}

typedef void* (*InterfacePointerCall)(uint16_t, void*, void*);

static void* invokeBuffPointer(uint16_t slot, void* iface, void* object)
{
	if (!configured_thread || !object)
	{
		return ((InterfacePointerCall)candidate_dispatch_config.pointer_invoker)(slot, iface, object);
	}
	void* pair = candidatePair(slot, iface, object);

	return ((void* (*)(void*, void*))(FIELD(void*, pair, 0)))(object, FIELD(void*, pair, 8));
}

static uint8_t invokeBuffMoveNext(uint16_t slot, void* iface, void* object)
{
	if (!configured_thread || !object)
	{
		return ((uint8_t (*)(uint16_t, void*, void*))candidate_dispatch_config.pointer_invoker)(slot, iface, object);
	}
	void* pair = candidatePair(slot, iface, object);

	return ((uint8_t (*)(void*, void*))(FIELD(void*, pair, 0)))(object, FIELD(void*, pair, 8));
}

API void* CollisionCandidatePointer(uint16_t slot, void* iface, void* object)
{
	if (configured_thread && object)
	{
		void* pair = candidatePair(slot, iface, object);
		void* method = FIELD(void*, pair, 0);

		if (slot == 1 && method == candidate_dispatch_config.transform_getter)
		{
			return FIELD(void*, object, 0xa0);
		}
		if (slot == 5 && method == candidate_dispatch_config.empty_collisions_getter)
		{
			return NULL;
		}
		return ((void* (*)(void*, void*))method)(object, FIELD(void*, pair, 8));
	}

	return ((InterfacePointerCall)candidate_dispatch_config.pointer_invoker)(slot, iface, object);
}

API float CollisionCandidateRadius(uint16_t slot, void* iface, void* object)
{
	if (configured_thread && object)
	{
		void* pair = candidatePair(slot, iface, object);
		void* method = FIELD(void*, pair, 0);

		if (slot == 0 && method == candidate_dispatch_config.radius_getter)
		{
			return FIELD(float, object, 0x150);
		}
		return ((float (*)(void*, void*))method)(object, FIELD(void*, pair, 8));
	}

	return ((float (*)(uint16_t, void*, void*))candidate_dispatch_config.float_invoker)(slot, iface, object);
}

API void* BuffEnumeratorPointer(uint16_t slot, void* iface, void* object)
{
	return invokeBuffPointer(slot, iface, object);
}

API uint8_t BuffEnumeratorMoveNext(uint16_t slot, void* iface, void* object)
{
	return invokeBuffMoveNext(slot, iface, object);
}

typedef struct InactiveBuffConfig
{
	void* position_getter;
	void* to_vector2;
	void* false_icon;
	void* position_initialized;
	void* vector_initialized;
	void* vector_class_slot;
	void* null_reference;
} InactiveBuffConfig;

static InactiveBuffConfig inactive_buff_config;
API void ConfigureInactiveBuff(const InactiveBuffConfig* c)
{
	inactive_buff_config = *c;
}

typedef uint8_t (*BuffPredicate)(void*, uint64_t, void*, void*);
API uint8_t InactiveBuffCheck(void* tower, void* support, void* class_owner)
{
	void* support_class = FIELD(void*, support, 0);
	void* tower_class = FIELD(void*, class_owner, 0);
	if (configured_thread && class_owner == tower &&
	    FIELD(void*, support_class, 0x9d8) == inactive_buff_config.false_icon &&
	    FIELD(void*, tower_class, 0x828) == inactive_buff_config.position_getter &&
	    FIELD(uint8_t, inactive_buff_config.position_initialized, 0) == 1 &&
	    FIELD(uint8_t, inactive_buff_config.vector_initialized, 0) == 1)
	{
		void* transform = FIELD(void*, tower, 0xa0);
		void* vector_class = FIELD(void*, inactive_buff_config.vector_class_slot, 0);
		if (transform && FIELD(void*, transform, 0x58) && vector_class && FIELD(int32_t, vector_class, 0xe4) != 0)
		{
			return 0;
		}
	}

	void* position =
	    ((void* (*)(void*, void*))(FIELD(void*, tower_class, 0x828)))(tower, FIELD(void*, tower_class, 0x830));
	if (!position)
	{
		((void (*)(void))inactive_buff_config.null_reference)();
		abort();
	}
	uint64_t xy = ((uint64_t (*)(void*, void*))inactive_buff_config.to_vector2)(position, NULL);
	support_class = FIELD(void*, support, 0);
	return ((BuffPredicate)(FIELD(void*, support_class, 0x9d8)))(support, xy, FIELD(void*, tower, 0x1a0),
	                                                             FIELD(void*, support_class, 0x9e0));
}

typedef struct RetirementConfig
{
	void* domain;
	void* attach;
	void* detach;
} RetirementConfig;

typedef struct RetirementWorker
{
	RetirementConfig api;
	HANDLE done;
	PTP_WORK work;
	GcHandle handles[256];
	uint32_t count, keep;
} RetirementWorker;

static void CALLBACK retireHandles(PTP_CALLBACK_INSTANCE instance, void* context, PTP_WORK work)
{
	RetirementWorker* worker = (RetirementWorker*)context;
	void* thread = ((void* (*)(void*))worker->api.attach)(worker->api.domain);
	(void)instance;
	(void)work;
	if (thread)
	{
		for (uint32_t i = 0; i < worker->count; ++i)
		{
			((HandleFree)config.handle_free)(worker->handles[i]);
			worker->handles[i] = 0;
		}
		((void (*)(void*))worker->api.detach)(thread);
	}
	SetEvent(worker->done);
}

static void finishRetirement(RetirementWorker* worker)
{
	for (uint32_t i = 0; i < worker->count; ++i)
		if (worker->handles[i])
		{
			((HandleFree)config.handle_free)(worker->handles[i]);
			worker->handles[i] = 0;
		}
	worker->count = 0;
}

static __declspec(thread) RetirementWorker* retirement = NULL;
API uint8_t StartRoundRetirement(const RetirementConfig* api, uint32_t capacity, uint32_t keep)
{
	if (!configured_thread || current_scope || retirement || !api || !api->domain || !api->attach || !api->detach ||
	    capacity < 32 || capacity > 256 || keep > 8)
		return 0;
	for (uint32_t i = capacity; i < pool_capacity; ++i)
		if (pool[i].busy)
			return 0;
	RetirementWorker* p = (RetirementWorker*)calloc(1, sizeof(*p));
	if (!p)
		return 0;
	p->api = *api;
	p->keep = keep;
	p->done = CreateEventW(NULL, TRUE, TRUE, NULL);
	if (!p->done)
	{
		free(p);
		return 0;
	}
	p->work = CreateThreadpoolWork(retireHandles, p, NULL);
	if (!p->work)
	{
		CloseHandle(p->done);
		free(p);
		return 0;
	}

	pool_capacity = capacity;
	retain_pool_classes = true;
	retirement = p;
	return 1;
}

API uint32_t RetireRoundObjects(void)
{
	RetirementWorker* p = retirement;
	if (!p || current_scope)
		return 0;
	if (WaitForSingleObject(p->done, 0) != WAIT_OBJECT_0)
	{
		return 0;
	}
	finishRetirement(p);
	// Recheck the cleared payload before giving the worker its only strong root.
	for (PoolSlot *slot = pool, *slot_end = pool + pool_capacity; slot < slot_end; ++slot)
		if (slot->handle && !slot->busy)
		{
			uint32_t earlier = 0;
			for (PoolSlot *candidate = pool, *candidate_end = pool + pool_capacity; candidate < candidate_end;
			     ++candidate)
			{
				if (candidate == slot)
					break;
				if (candidate->handle && !candidate->busy && candidate->klass == slot->klass)
					++earlier;
			}
			if (earlier < p->keep)
				continue;
			bool cleared = true;
			for (uint32_t o = 16; o < slot->bytes; ++o)
				if (FIELD(uint8_t, slot->object, o))
				{
					cleared = false;
					break;
				}
			if (!cleared)
				continue;
			p->handles[p->count++] = slot->handle;
			memset(slot, 0, sizeof(*slot));
		}
	for (ComparerSlot* slot = comparers; slot < comparers + 1024; ++slot)
		if (slot->handle && p->count < 256 && !((HandleTarget)config.handle_target)(slot->handle))
		{
			p->handles[p->count++] = slot->handle;
			memset(slot, 0, sizeof(*slot));
		}
	if (!p->count)
		return 0;
	uint32_t count = p->count;

	ResetEvent(p->done);
	SubmitThreadpoolWork(p->work);
	return count;
}

API void StopRoundRetirement(void)
{
	RetirementWorker* p = retirement;
	if (!p)
		return;
	WaitForThreadpoolWorkCallbacks(p->work, FALSE);
	finishRetirement(p);

	CloseThreadpoolWork(p->work);
	CloseHandle(p->done);
	free(p);
	retirement = NULL;
	retain_pool_classes = false;
	for (uint32_t i = 32; i < pool_capacity; ++i)
		if (pool[i].handle && !pool[i].busy)
		{
			((HandleFree)config.handle_free)(pool[i].handle);
			memset(&pool[i], 0, sizeof(pool[i]));
		}
	// A nested shutdown must not hide active leases.
	bool live = false;
	for (uint32_t i = 32; i < pool_capacity; ++i)
		live = live || pool[i].busy;
	if (!live)
		pool_capacity = 32;
}

typedef struct SaveBufferConfig
{
	void* rent;
	void* give_back;
} SaveBufferConfig;

static SaveBufferConfig save_buffer_config;

typedef struct SaveBufferSlot
{
	void* array;
	GcHandle root;
	uint64_t length;
} SaveBufferSlot;

static __declspec(thread) SaveBufferSlot save_buffers[8];
static __declspec(thread) uint64_t save_buffer_bytes;
static const uint64_t save_buffer_budget = 128 * 1024;
API void ConfigureSaveBuffers(const SaveBufferConfig* c)
{
	save_buffer_config = *c;
}

static void clearSaveBuffers(void)
{
	for (SaveBufferSlot* s = save_buffers; s < save_buffers + 8; ++s)
	{
		if (s->root)
			((HandleFree)config.handle_free)(s->root);
		memset(s, 0, sizeof(*s));
	}
	save_buffer_bytes = 0;
}

API void* RentJsonWriteBuffer(void* custom_pool, int32_t minimum, void* info)
{
	typedef void* (*Rent)(void*, int32_t, void*);
	if (configured_thread && !custom_pool && minimum > 0)
	{
		SaveBufferSlot* best = NULL;
		for (SaveBufferSlot* s = save_buffers; s < save_buffers + 8; ++s)
			if (s->root && s->length >= (uint64_t)minimum && (!best || s->length < best->length))
				best = s;
		if (best)
		{
			void* result = best->array;
			GcHandle root = best->root;
			save_buffer_bytes -= best->length * 2;
			memset(best, 0, sizeof(*best));
			// The stack and writer root the array; pooling would retain old buffers after growth.
			((HandleFree)config.handle_free)(root);

			return result;
		}
	}

	return ((Rent)save_buffer_config.rent)(custom_pool, minimum, info);
}

API void ReturnJsonWriteBuffer(void* custom_pool, void* array, void* info)
{
	typedef void (*Return)(void*, void*, void*);
	if (!configured_thread || custom_pool || !array)
	{
		((Return)save_buffer_config.give_back)(custom_pool, array, info);
		return;
	}
	const uint64_t length = FIELD(uint64_t, array, ARRAY_LENGTH_OFFSET);
	if (length < 35 || length > save_buffer_budget / 2 || save_buffer_bytes + length * 2 > save_buffer_budget)
	{
		((Return)save_buffer_config.give_back)(custom_pool, array, info);
		return;
	}
	SaveBufferSlot* empty = NULL;
	for (SaveBufferSlot* s = save_buffers; s < save_buffers + 8; ++s)
	{
		if (s->array == array)
			return;
		if (!s->root && !empty)
			empty = s;
	}
	if (!empty)
		return;
	GcHandle root = ((HandleNew)config.handle_new)(array, false);
	if (!root)
		return;
	memset((char*)array + ARRAY_DATA_OFFSET, 0, (size_t)(length * 2));
	*empty = (SaveBufferSlot){array, root, length};
	save_buffer_bytes += length * 2;
}

typedef struct TextCacheConfig
{
	void* default_comparer;
	void* string_class;
	void* original;
	void* find_entry;
	void* string_equals;
} TextCacheConfig;

static TextCacheConfig text_cache_config;

typedef struct TextCacheSlot
{
	void* dictionary;
	void* entries;
	int32_t index;
} TextCacheSlot;

static __declspec(thread) TextCacheSlot text_cache[512];
API void ConfigureTextCache(const TextCacheConfig* c)
{
	text_cache_config = *c;
}

static void clearTextCache(void)
{
	for (TextCacheSlot* s = text_cache; s < text_cache + 512; ++s)
		*s = (TextCacheSlot){.index = -1};
}

typedef uint8_t (*TextDictionaryGetter)(void*, void*, void**, void*);

static uint8_t textDictionaryOriginal(void* dictionary, void* key, void** value, void* info)
{
	return ((TextDictionaryGetter)text_cache_config.original)(dictionary, key, value, info);
}

static void* dictionaryEntry(void* entries, int32_t index)
{
	return (char*)entries + ARRAY_DATA_OFFSET + (size_t)index * 24;
}

API uint8_t CachedLocalizationLookup(void* dictionary, void* key, void** value, void* info)
{
	if (!configured_thread || !dictionary || !key || !value || !info ||
	    FIELD(void*, key, 0) != text_cache_config.string_class)
		return textDictionaryOriginal(dictionary, key, value, info);
	void* comparer = FIELD(void*, dictionary, 0x30);
	if (comparer && comparer != text_cache_config.default_comparer)
		return textDictionaryOriginal(dictionary, key, value, info);
	void* entries = FIELD(void*, dictionary, 0x18);
	int32_t count = FIELD(int32_t, dictionary, 0x20);
	if (!entries || count < 0 || (uint64_t)count > FIELD(uint64_t, entries, ARRAY_LENGTH_OFFSET))
		return textDictionaryOriginal(dictionary, key, value, info);
	int32_t length = FIELD(int32_t, key, 0x10);
	if (length < 0)
		return textDictionaryOriginal(dictionary, key, value, info);
	uint32_t fingerprint = (uint32_t)length * 0x9e3779b1u;
	if (length)
		fingerprint ^= (uint32_t)(FIELD(uint16_t, key, 0x14)) * 0x85ebca6bu ^
		               (uint32_t)(FIELD(uint16_t, key, 0x14 + (size_t)(length - 1) * 2)) * 0xc2b2ae35u;
	TextCacheSlot* slot = &text_cache[(((uintptr_t)dictionary >> 4) ^ fingerprint) & 511];
	// Recycled addresses can make cached indices stale.

	if (slot->dictionary == dictionary && slot->entries == entries && slot->index >= 0 && slot->index < count)
	{
		void* entry = dictionaryEntry(entries, slot->index);
		void* stored_key = FIELD(void*, entry, 8);
		if (FIELD(int32_t, entry, 0) >= 0 && stored_key &&
		    FIELD(void*, stored_key, 0) == text_cache_config.string_class &&
		    (stored_key == key ||
		     ((uint8_t (*)(void*, void*, void*))text_cache_config.string_equals)(stored_key, key, NULL)))
		{
			*value = FIELD(void*, entry, 16);

			return 1;
		}
	}

	void* klass = FIELD(void*, info, 0x20);
	void* context = klass ? FIELD(void*, klass, 0xc0) : NULL;
	void* find_info = context ? FIELD(void*, context, 0x108) : NULL;
	if (!find_info)
		return textDictionaryOriginal(dictionary, key, value, info);
	int32_t index = ((int32_t (*)(void*, void*, void*))text_cache_config.find_entry)(dictionary, key, find_info);
	if (index < 0)
	{
		*value = NULL;
		*slot = (TextCacheSlot){.index = -1};
		return 0;
	}
	if (FIELD(void*, dictionary, 0x18) != entries || index >= count)
		return textDictionaryOriginal(dictionary, key, value, info);
	void* entry = dictionaryEntry(entries, index);
	*value = FIELD(void*, entry, 16);
	if (FIELD(int32_t, entry, 0) >= 0)
		*slot = (TextCacheSlot){dictionary, entries, index};
	else
		*slot = (TextCacheSlot){.index = -1};
	return 1;
}

typedef struct RoundBufferConfig
{
	void* array_new;
	void* enumerable_to_array;
	void* list_to_array;
	void* buffer_ctor;
	void* tower_array;
	void* behavior_array;
	void* tower_enumerable;
} RoundBufferConfig;

static RoundBufferConfig round_buffer_config;
static void* round_end_original;

typedef struct RoundBufferSlot
{
	void* array;
	void* klass;
	GcHandle root;
	uintptr_t length;
	bool busy;
	uint64_t age;
} RoundBufferSlot;

static __declspec(thread) RoundBufferSlot round_buffers[16];
static __declspec(thread) uint64_t round_buffer_bytes;
static __declspec(thread) uint64_t round_buffer_age;
static const uintptr_t round_buffer_budget = 512 * 1024;
typedef struct RoundBufferScope RoundBufferScope;
static __declspec(thread) RoundBufferScope* round_buffer_scope;
static __declspec(thread) bool round_array_allowed;
static __declspec(thread) void* round_ctor_owner;

static void returnRoundBuffer(RoundBufferSlot* s)
{
	for (uintptr_t i = 0; i < s->length; ++i)
		storeReference(s->array, (void**)((char*)s->array + ARRAY_DATA_OFFSET + i * 8), NULL);
	s->busy = false;
}

struct RoundBufferScope
{
	RoundBufferScope* previous;
	RoundBufferSlot* leases[16];
	size_t count;
};

API void ConfigureRoundBuffers(const RoundBufferConfig* c)
{
	round_buffer_config = *c;
}

API void SetRoundEndOriginal(void* original)
{
	round_end_original = original;
}

API void ScopedRoundEnd(void* simulation, int32_t round, int32_t next, void* info)
{
	typedef void (*Original)(void*, int32_t, int32_t, void*);
	if (!configured_thread)
	{
		((Original)round_end_original)(simulation, round, next, info);
		return;
	}
	RoundBufferScope scope = {0};
	bool previous = round_array_allowed;
	scope.previous = round_buffer_scope;
	round_buffer_scope = &scope;
	// Nested rounds can allocate only after their own snapshot calls.
	round_array_allowed = false;
	__try
	{
		((Original)round_end_original)(simulation, round, next, info);
	}
	__finally
	{
		round_array_allowed = previous;
		for (size_t i = 0; i < scope.count; ++i)
			returnRoundBuffer(scope.leases[i]);
		round_buffer_scope = scope.previous;
	}
}

static void* allocateRoundArray(void* klass, uintptr_t length)
{
	return ((void* (*)(void*, uintptr_t))round_buffer_config.array_new)(klass, length);
}

static void evictRoundBuffer(RoundBufferSlot* slot)
{
	round_buffer_bytes -= ARRAY_DATA_OFFSET + slot->length * 8;
	((HandleFree)config.handle_free)(slot->root);
	memset(slot, 0, sizeof(*slot));
}

static void* rentRoundArray(void* klass, uintptr_t length, bool permitted)
{
	if (!configured_thread || !round_buffer_scope || !permitted || round_buffer_scope->count == 16 ||
	    (klass != round_buffer_config.tower_array && klass != round_buffer_config.behavior_array) ||
	    length > (round_buffer_budget - 0x20) / 8)
		return allocateRoundArray(klass, length);
	for (RoundBufferSlot* s = round_buffers; s < round_buffers + 16; ++s)
		if (s->root && !s->busy && s->klass == klass && s->length == length)
		{
			s->busy = true;
			s->age = ++round_buffer_age;
			round_buffer_scope->leases[round_buffer_scope->count++] = s;

			return s->array;
		}
	RoundBufferSlot* empty = NULL;
	const uintptr_t bytes = ARRAY_DATA_OFFSET + length * 8;

	while (true)
	{
		empty = NULL;
		RoundBufferSlot* oldest = NULL;
		for (RoundBufferSlot* s = round_buffers; s < round_buffers + 16; ++s)
		{
			if (!s->root && !empty)
				empty = s;
			if (s->root && !s->busy && (!oldest || s->age < oldest->age))
				oldest = s;
		}
		if (empty && round_buffer_bytes + bytes <= round_buffer_budget)
			break;
		if (!oldest)
			return allocateRoundArray(klass, length);
		evictRoundBuffer(oldest);
	}
	void* result = ((void* (*)(void*, uintptr_t))round_buffer_config.array_new)(klass, length);
	if (!result)
		return result;
	GcHandle root = ((HandleNew)config.handle_new)(result, false);
	if (!root)
		return result;
	*empty = (RoundBufferSlot){result, klass, root, length, true, ++round_buffer_age};
	round_buffer_bytes += bytes;
	round_buffer_scope->leases[round_buffer_scope->count++] = empty;

	return result;
}

API void* RoundSnapshotArrayNew(void* klass, uintptr_t length)
{
	return rentRoundArray(klass, length, round_array_allowed);
}

API void* RoundConstructorArrayNew(void* klass, uintptr_t length, void* owner)
{
	return rentRoundArray(klass, length, owner && owner == round_ctor_owner);
}

API void* RoundTowerSnapshot(void* source, void* info)
{
	bool previous = round_array_allowed;
	void* result;
	round_array_allowed =
	    round_buffer_scope && source && FIELD(void*, source, 0) == round_buffer_config.tower_enumerable;
	__try
	{
		result = ((void* (*)(void*, void*))round_buffer_config.enumerable_to_array)(source, info);
	}
	__finally
	{
		round_array_allowed = previous;
	}
	return result;
}

API void* RoundBehaviorSnapshot(void* source, void* info)
{
	bool previous = round_array_allowed;
	void* result;
	round_array_allowed = round_buffer_scope != NULL;
	__try
	{
		result = ((void* (*)(void*, void*))round_buffer_config.list_to_array)(source, info);
	}
	__finally
	{
		round_array_allowed = previous;
	}
	return result;
}

API void* RoundSnapshotBufferCtor(void* output, void* source, void* info)
{
	void* previous_owner = round_ctor_owner;
	bool previous_permission = round_array_allowed;
	round_ctor_owner = round_array_allowed ? output : NULL;
	// Nested callbacks must not allocate from the outer scope. Guarded calls pass its buffer in R8.
	round_array_allowed = false;
	__try
	{
		((void (*)(void*, void*, void*))round_buffer_config.buffer_ctor)(output, source, info);
	}
	__finally
	{
		round_array_allowed = previous_permission;
		round_ctor_owner = previous_owner;
	}
	return output;
}

static void clearRoundBuffers(void)
{
	if (round_buffer_scope)
		return;
	for (RoundBufferSlot* s = round_buffers; s < round_buffers + 16; ++s)
		if (!s->busy)
		{
			if (s->root)
				((HandleFree)config.handle_free)(s->root);
			memset(s, 0, sizeof(*s));
		}
	round_buffer_bytes = 0;
}

typedef struct BuffReuseConfig
{
	void* banana_class;
	void* get_child;
	void* is_instance;
} BuffReuseConfig;

static BuffReuseConfig buff_reuse_config;
typedef void* (*BananaQuery)(void*, void*, void*, void*, int32_t, void*);
static BananaQuery banana_query_original;
API void ConfigureBuffReuse(const BuffReuseConfig* c)
{
	buff_reuse_config = *c;
}

API void SetBananaQueryOriginal(void* p)
{
	banana_query_original = ((BananaQuery)p);
}

typedef struct BananaKey
{
	uintptr_t values[10];
} BananaKey;

typedef struct BananaSlot
{
	BananaKey key;
	unsigned char result[40];
	bool valid;
} BananaSlot;

typedef struct BindingSlot
{
	void* parent;
	void* info;
	void* list;
	void* array;
	void* flags;
	void* target;
	void* result;
	int32_t count, version, index;
} BindingSlot;

static __declspec(thread) BananaSlot banana_slots[512];
static __declspec(thread) BindingSlot binding_slots[512];
static __declspec(thread) BindingSlot binding_second_slots[512];
static bool binding_second_enabled;
static __declspec(thread) bool banana_initialized;

static void clearBuffReuse(void)
{
	for (BananaSlot* s = banana_slots; s < banana_slots + 512; ++s)
		memset(s, 0, sizeof(*s));
	for (BindingSlot* s = binding_slots; s < binding_slots + 512; ++s)
		*s = (BindingSlot){.index = -1};
	for (BindingSlot* s = binding_second_slots; s < binding_second_slots + 512; ++s)
		*s = (BindingSlot){.index = -1};
	banana_initialized = false;
}

API void ConfigureBindingSecondChance(int32_t enabled)
{
	binding_second_enabled = enabled != 0;
	for (BindingSlot* s = binding_second_slots; s < binding_second_slots + 512; ++s)
		*s = (BindingSlot){.index = -1};
}

static bool bananaKey(void* behavior, void* model, BananaKey* key)
{
	if (!behavior || !model || FIELD(void*, behavior, 0) != buff_reuse_config.banana_class)
		return false;
	void* source = FIELD(void*, behavior, 0x60);
	if (!source)
		return false;
	void* source_model = FIELD(void*, source, 0x1a0);
	if (!source_model)
		return false;
	void* tiers = FIELD(void*, model, 0x68);
	if (!tiers || FIELD(uint64_t, tiers, ARRAY_LENGTH_OFFSET) == 0)
		return false;
	*key = (BananaKey){{(uintptr_t)behavior, (uintptr_t)model, (uintptr_t)source, (uintptr_t)source_model,
	                    (uintptr_t)(FIELD(void*, source_model, 0x28)), (uintptr_t)(FIELD(void*, model, 0x28)),
	                    (uintptr_t)tiers, FIELD(uint64_t, tiers, ARRAY_LENGTH_OFFSET), FIELD(uint8_t, source, 0xbc),
	                    (uint32_t)(FIELD(int32_t, tiers, 0x20))}};
	return true;
}

API void* ReusedBananaPlacementQuery(void* output, void* behavior, void* model, void* position, int32_t owner,
                                     void* info)
{
	if (configured_thread && output && behavior && model && FIELD(void*, behavior, 0) == buff_reuse_config.banana_class)
	{
		if (!banana_initialized)
		{
			void* result = banana_query_original(output, behavior, model, position, owner, info);
			banana_initialized = true;
			return result;
		}
		void* source = FIELD(void*, behavior, 0x60);
		void* source_model = source ? FIELD(void*, source, 0x1a0) : NULL;
		if (source_model && FIELD(void*, source_model, 0x28) == FIELD(void*, model, 0x28))
		{
			if (FIELD(uint8_t, source, 0xbc))
			{
				memset(output, 0, 40);

				return output;
			}
			void* tiers = FIELD(void*, model, 0x68);
			if (tiers && FIELD(uint64_t, tiers, ARRAY_LENGTH_OFFSET) > 0)
			{
				memset(output, 0, 40);
				FIELD(uint8_t, output, 0x18) = 1;
				if (FIELD(int32_t, tiers, 0x20) >= 4)
				{
					FIELD(void*, output, 0x10) = source;
					FIELD(uint8_t, output, 0x19) = 1;
				}

				return output;
			}
		}
	}
	BananaKey key;
	if (!configured_thread || !output || !bananaKey(behavior, model, &key))
	{
		return banana_query_original(output, behavior, model, position, owner, info);
	}
	BananaSlot* slot = &banana_slots[(((uintptr_t)behavior >> 4) ^ ((uintptr_t)model >> 3)) & 511];
	if (slot->valid && memcmp(&slot->key, &key, sizeof(key)) == 0)
	{
		memcpy(output, slot->result, 40);

		return output;
	}

	void* result = banana_query_original(output, behavior, model, position, owner, info);
	BananaKey after;
	if (result == output && bananaKey(behavior, model, &after) && memcmp(&after, &key, sizeof(key)) == 0)
	{
		slot->key = key;
		memcpy(slot->result, output, 40);
		slot->valid = true;
	}
	else
		memset(slot, 0, sizeof(*slot));
	return result;
}

typedef struct BindingView
{
	void* list;
	void* array;
	void* flags;
	void* target;
	int32_t count, version;
} BindingView;

static bool bindingView(void* parent, void* info, BindingView* view)
{
	if (!parent || !info)
		return false;
	void* context = FIELD(void*, info, 0x38);
	if (!context)
		return false;
	void* target = FIELD(void*, context, 0);
	void* is_info = FIELD(void*, context, 8);
	if (!target || !is_info || !(FIELD(uint8_t, target, 0x135) & 1))
		return false;
	void* is_context = FIELD(void*, is_info, 0x38);
	if (!is_context)
		return false;
	void* cache_class = FIELD(void*, is_context, 8);
	if (!cache_class || !(FIELD(uint8_t, cache_class, 0x135) & 1) || !FIELD(int32_t, cache_class, 0xe4))
		return false;
	void* statics = FIELD(void*, cache_class, 0xb8);
	if (!statics)
		return false;
	void* flags = FIELD(void*, statics, 8);
	void* list = FIELD(void*, parent, 0x18);
	if (!flags || !list)
		return false;
	void* array = FIELD(void*, list, 0x10);
	int32_t count = FIELD(int32_t, list, 0x18);
	if (!array || count < 0 || (uint64_t)count > FIELD(uint64_t, array, ARRAY_LENGTH_OFFSET))
		return false;
	*view = (BindingView){list, array, flags, target, count, FIELD(int32_t, list, 0x1c)};
	return true;
}

static bool firstBinding(const BindingView* v, int32_t* index, void** child)
{
	// Child types and flags can change without changing the List version.
	for (int32_t i = 0; i < v->count; ++i)
	{
		void* current = FIELD(void*, v->array, ARRAY_DATA_OFFSET + (size_t)i * 8);
		if (!current)
			return false;
		uint32_t type = (uint32_t)(FIELD(int32_t, current, 0x10));
		if (type >= FIELD(uint64_t, v->flags, ARRAY_LENGTH_OFFSET))
			return false;
		if (FIELD(uint8_t, v->flags, ARRAY_DATA_OFFSET + type))
		{
			*index = i;
			*child = current;
			return true;
		}
	}
	return false;
}

static void* buffIndicatorChildOriginal(void* parent, void* info)
{
	return ((void* (*)(void*, void*))buff_reuse_config.get_child)(parent, info);
}

API void* ReusedBuffIndicatorChild(void* parent, void* info)
{
	BindingView view;
	if (!configured_thread || !bindingView(parent, info, &view))
	{
		return buffIndicatorChildOriginal(parent, info);
	}
	BindingSlot* selected = &binding_slots[(((uintptr_t)parent >> 4) ^ ((uintptr_t)info >> 3)) & 511];
	if (binding_second_enabled && selected->parent && (selected->parent != parent || selected->info != info))
	{
		uintptr_t p = (uintptr_t)parent, m = (uintptr_t)info;
		selected = &binding_second_slots[((p >> 4) ^ (p >> 13) ^ (m >> 3) ^ (m >> 17)) & 511];
	}
	BindingSlot* slot = selected;
	int32_t index = -1;
	void* child = NULL;
	if (slot->parent == parent && slot->info == info && slot->list == view.list && slot->array == view.array &&
	    slot->flags == view.flags && slot->target == view.target && slot->count == view.count &&
	    slot->version == view.version && firstBinding(&view, &index, &child) && index == slot->index &&
	    child == slot->result)
	{
		void* result = ((void* (*)(void*, void*))buff_reuse_config.is_instance)(child, view.target);
		if (FIELD(int32_t, view.list, 0x1c) == view.version)
		{
			return result;
		}
	}

	void* result = buffIndicatorChildOriginal(parent, info);
	BindingView after;
	if (result && bindingView(parent, info, &after) && after.list == view.list && after.array == view.array &&
	    after.flags == view.flags && after.target == view.target && after.count == view.count &&
	    after.version == view.version && firstBinding(&after, &index, &child) && child == result)
	{
		*slot = (BindingSlot){parent,      info,   view.list,  view.array,   view.flags,
		                      view.target, result, view.count, view.version, index};
	}
	else
		*slot = (BindingSlot){.index = -1};
	return result;
}

API void* ReusedMutatorBuffChild(void* parent, void* info)
{
	void* result = ReusedBuffIndicatorChild(parent, info);

	return result;
}

typedef struct TravelMathConfig
{
	void* geometry_class;
	void* multiply;
	void* add;
} TravelMathConfig;

static TravelMathConfig travel_math;
static void** travel_math_class_slot;
static uint8_t* travel_multiply_initialized;
static uint8_t* travel_add_initialized;
API void ConfigureTravelMath(const TravelMathConfig* c)
{
	travel_math = *c;
	travel_math_class_slot = NULL;
}

static bool travelMathReady(void* boxed)
{
	void* klass = travel_math_class_slot ? *travel_math_class_slot : travel_math.geometry_class;
	return configured_thread && boxed && klass && FIELD(int32_t, klass, 0xe4) != 0;
}

typedef void* (*VectorScale)(void*, void*, float, void*);
API void* TravelMultiplyFast(void* output, void* boxed, float scale, void* info)
{
	if (!travelMathReady(boxed))
	{
		return ((VectorScale)travel_math.multiply)(output, boxed, scale, info);
	}
	__m128 scalar = _mm_set_ss(scale);
	__m128 x = _mm_mul_ss(_mm_load_ss((float*)boxed + 4), scalar);
	__m128 y = _mm_mul_ss(_mm_load_ss((float*)boxed + 5), scalar);
	__m128 z = _mm_mul_ss(_mm_load_ss((float*)boxed + 6), scalar);
	_mm_store_ss((float*)output, x);
	_mm_store_ss((float*)output + 1, y);
	_mm_store_ss((float*)output + 2, z);

	return output;
}

typedef void* (*VectorAdd)(void*, void*, void*, void*);
API void* TravelAddFast(void* output, void* boxed, void* delta, void* info)
{
	if (!travelMathReady(boxed))
	{
		return ((VectorAdd)travel_math.add)(output, boxed, delta, info);
	}
	// Separate multiply and add preserve rounding, NaN payloads and signed zero.
	__m128 x = _mm_add_ss(_mm_load_ss((float*)boxed + 4), _mm_load_ss((float*)delta));
	__m128 y = _mm_add_ss(_mm_load_ss((float*)boxed + 5), _mm_load_ss((float*)delta + 1));
	__m128 z = _mm_add_ss(_mm_load_ss((float*)boxed + 6), _mm_load_ss((float*)delta + 2));
	_mm_store_ss((float*)output, x);
	_mm_store_ss((float*)output + 1, y);
	_mm_store_ss((float*)output + 2, z);

	return output;
}

API void* TravelMultiplyAddFast(void* output, void* direction, float scale, void* position)
{
	if (!travelMathReady(direction) || !position)
	{
		((VectorScale)travel_math.multiply)(output, direction, scale, NULL);
		return ((VectorAdd)travel_math.add)(output, position, output, NULL);
	}
	__m128 scalar = _mm_set_ss(scale);
	__m128 dx = _mm_mul_ss(_mm_load_ss((float*)direction + 4), scalar);
	__m128 dy = _mm_mul_ss(_mm_load_ss((float*)direction + 5), scalar);
	__m128 dz = _mm_mul_ss(_mm_load_ss((float*)direction + 6), scalar);
	__m128 x = _mm_add_ss(_mm_load_ss((float*)position + 4), dx);
	__m128 y = _mm_add_ss(_mm_load_ss((float*)position + 5), dy);
	__m128 z = _mm_add_ss(_mm_load_ss((float*)position + 6), dz);
	_mm_store_ss((float*)output, x);
	_mm_store_ss((float*)output + 1, y);
	_mm_store_ss((float*)output + 2, z);

	return output;
}

API void ConfigureCurrentTravelMath(void** class_slot, void* multiply, void* add, uint8_t* multiply_initialized,
                                    uint8_t* add_initialized)
{
	travel_math = (TravelMathConfig){NULL, multiply, add};
	travel_math_class_slot = class_slot;
	travel_multiply_initialized = multiply_initialized;
	travel_add_initialized = add_initialized;
}

API void* CurrentTravelMultiplyFast(void* output, void* boxed, float scale, void* info)
{
	if (!*travel_multiply_initialized || (_mm_getcsr() & 0x1f80) != 0x1f80)
		return ((VectorScale)travel_math.multiply)(output, boxed, scale, info);
	return TravelMultiplyFast(output, boxed, scale, info);
}

API void* CurrentTravelAddFast(void* output, void* boxed, void* delta, void* info)
{
	if (!*travel_add_initialized || (_mm_getcsr() & 0x1f80) != 0x1f80)
		return ((VectorAdd)travel_math.add)(output, boxed, delta, info);
	return TravelAddFast(output, boxed, delta, info);
}

API void* CurrentTravelMultiplyAddFast(void* output, void* direction, float scale, void* position)
{
	if (!*travel_multiply_initialized || !*travel_add_initialized || !travelMathReady(direction) || !position ||
	    (_mm_getcsr() & 0x1f80) != 0x1f80)
	{
		((VectorScale)travel_math.multiply)(output, direction, scale, NULL);
		return ((VectorAdd)travel_math.add)(output, position, output, NULL);
	}
	return TravelMultiplyAddFast(output, direction, scale, position);
}

typedef struct EntityPositionConfig
{
	void* entity_getter;
	void* common_getter;
	void* entity_initialized;
	void* common_initialized;
	void* common_iface_slot;
} EntityPositionConfig;

static EntityPositionConfig entity_position_config;

typedef struct PositionBinding
{
	void* klass;
	void* table;
	void* iface;
	uint16_t index;
	int32_t offset;
} PositionBinding;

static __declspec(thread) PositionBinding position_binding;
API void ConfigureEntityPosition(const EntityPositionConfig* c)
{
	entity_position_config = *c;
	memset(&position_binding, 0, sizeof(position_binding));
}

static void* positionMethodPair(void* klass, int32_t offset)
{
	return (char*)klass + CLASS_VTABLE_OFFSET + (int64_t)offset * VIRTUAL_METHOD_SIZE;
}

static void* positionPair(void* root, void* iface)
{
	void* klass = FIELD(void*, root, 0);
	void* table = FIELD(void*, klass, 0xb0);
	uint16_t count = FIELD(uint16_t, klass, 0x12e);

	PositionBinding* cached = &position_binding;
	if (cached->klass == klass && cached->table == table && cached->iface == iface && cached->index < count)
	{
		void* entry = (char*)table + (size_t)cached->index * 16;
		if (FIELD(void*, entry, 0) == iface && FIELD(int32_t, entry, 8) == cached->offset)
			return positionMethodPair(klass, cached->offset);
	}
	for (uint16_t index = 0; index < count; ++index)
	{
		void* entry = (char*)table + (size_t)index * 16;
		if (FIELD(void*, entry, 0) == iface)
		{
			int32_t offset = FIELD(int32_t, entry, 8);
			*cached = (PositionBinding){klass, table, iface, index, offset};
			return positionMethodPair(klass, offset);
		}
	}
	memset(cached, 0, sizeof(*cached));
	return NULL;
}

static bool tryEntityPosition(void* behavior, void* method, void** result)
{
	EntityPositionConfig* c = &entity_position_config;
	if (!configured_thread || method != c->entity_getter || !FIELD(uint8_t, c->entity_initialized, 0) ||
	    !FIELD(uint8_t, c->common_initialized, 0))
		return false;
	void* root = FIELD(void*, behavior, 0x58);
	if (!root)
		return false;
	void* pair = positionPair(root, FIELD(void*, c->common_iface_slot, 0));
	if (!pair || FIELD(void*, pair, 0) != c->common_getter)
		return false;
	void* transform = FIELD(void*, root, 0xa0);
	if (!transform)
		return false;
	*result = FIELD(void*, transform, 0x58);
	return true;
}

API void* TravelEntityPosition(void* behavior)
{
	void* klass = FIELD(void*, behavior, 0);
	void* method = FIELD(void*, klass, 0x548);
	void* result;
	if (tryEntityPosition(behavior, method, &result))
	{
		return result;
	}

	return ((void* (*)(void*, void*))method)(behavior, FIELD(void*, klass, 0x550));
}

API void* TravelEntityPositions(void* behavior, void** first)
{
	void* klass = FIELD(void*, behavior, 0);
	void* result;
	if (tryEntityPosition(behavior, FIELD(void*, klass, 0x548), &result))
	{
		// Both verified getters read this field, with no intervening callback.

		*first = result;
		return result;
	}
	*first = TravelEntityPosition(behavior);
	// Reload after the first callback; an override can change the class, parent or transform.
	return TravelEntityPosition(behavior);
}

static void* travel_direction_getter;
static void* travel_direction_original;
API void ConfigureTravelDirection(void* getter, void* original)
{
	travel_direction_getter = getter;
	travel_direction_original = original;
}

API void* TravelCurrentPosition(void* behavior)
{
	void* klass = FIELD(void*, behavior, 0);
	void* method = FIELD(void*, klass, 0x558);
	void* result;
	if (FIELD(volatile uint64_t, method, 0) == 0x3d8020ec83485340ull &&
	    FIELD(volatile uint64_t, entity_position_config.common_getter, 0) == 0x3d8020ec83485340ull &&
	    tryEntityPosition(behavior, method, &result))
		return result;
	return ((void* (*)(void*, void*))method)(behavior, FIELD(void*, klass, 0x560));
}

API void* TravelCurrentPositionPair(void* behavior, void** first)
{
	void* klass = FIELD(void*, behavior, 0);
	void* method = FIELD(void*, klass, 0x558);
	void* result;
	if (FIELD(volatile uint64_t, method, 0) == 0x3d8020ec83485340ull &&
	    FIELD(volatile uint64_t, entity_position_config.common_getter, 0) == 0x3d8020ec83485340ull &&
	    tryEntityPosition(behavior, method, &result))
	{
		*first = result;
		return result;
	}
	*first = TravelCurrentPosition(behavior);
	return TravelCurrentPosition(behavior);
}

API void* TravelCurrentDirection(uint16_t slot, void* iface, void* object)
{
	if (configured_thread && object && slot == 0x17 && iface &&
	    iface == FIELD(void*, entity_position_config.common_iface_slot, 0))
	{
		void* klass = FIELD(void*, object, 0);
		void* pair = positionPair(object, iface);
		uintptr_t offset = (uintptr_t)pair - (uintptr_t)klass - CLASS_VTABLE_OFFSET;
		if (pair && offset <= (65535 - slot) * VIRTUAL_METHOD_SIZE)
		{
			pair = (char*)pair + slot * VIRTUAL_METHOD_SIZE;
			if (FIELD(void*, pair, 0) == travel_direction_getter &&
			    FIELD(volatile uint64_t, travel_direction_getter, 0) == 0xc3000000c0818b48ull)
				return FIELD(void*, object, 0xc0);
		}
	}
	return ((void* (*)(uint16_t, void*, void*))travel_direction_original)(slot, iface, object);
}

typedef struct BuffMutationConfig
{
	void* original;
	void* initialized;
	void* info_slot;
	void* hash_method;
	void* equals_method;
} BuffMutationConfig;

static BuffMutationConfig buff_mutation;
API void ConfigureBuffMutation(const BuffMutationConfig* c)
{
	buff_mutation = *c;
}

static void* mutationContext(void* info)
{
	void* klass = info ? FIELD(void*, info, 0x20) : NULL;
	return klass ? FIELD(void*, klass, 0xc0) : NULL;
}

static uint8_t buffMutationOriginal(void* mutator, void* model, void* info)
{
	return ((uint8_t (*)(void*, void*, void*))buff_mutation.original)(mutator, model, info);
}

API uint8_t BuffMutationLookupFast(void* mutator, void* model, void* info)
{
	if (!configured_thread || !mutator || !model || !buff_mutation.initialized ||
	    !FIELD(uint8_t, buff_mutation.initialized, 0))
		return buffMutationOriginal(mutator, model, info);
	void* set = FIELD(void*, mutator, 0x68);
	if (!set)
		return buffMutationOriginal(mutator, model, info);
	void* buckets = FIELD(void*, set, 0x10);
	if (!buckets)
	{
		return 0;
	}
	void* method = buff_mutation.info_slot ? FIELD(void*, buff_mutation.info_slot, 0) : NULL;
	void* context = mutationContext(method);
	if (!context)
		return buffMutationOriginal(mutator, model, info);
	void* iface = FIELD(void*, context, 0x20);
	void* hash_info = FIELD(void*, context, 0xb0);
	void* hash_context = mutationContext(hash_info);
	if (!iface || !(FIELD(uint8_t, iface, 0x135) & 1) || !hash_context || FIELD(void*, hash_context, 0x20) != iface)
		return buffMutationOriginal(mutator, model, info);
	void* comparer = FIELD(void*, set, 0x30);
	if (!comparer)
		return buffMutationOriginal(mutator, model, info);
	void* klass = FIELD(void*, comparer, 0);
	void* table = klass ? FIELD(void*, klass, 0xb0) : NULL;
	if (!klass)
		return buffMutationOriginal(mutator, model, info);
	uint16_t interfaces = FIELD(uint16_t, klass, 0x12e);
	int32_t offset = 0;
	bool found = false;
	if (interfaces && !table)
		return buffMutationOriginal(mutator, model, info);
	for (uint16_t i = 0; i < interfaces; ++i)
		if (FIELD(void*, table, (size_t)i * 16) == iface)
		{
			offset = FIELD(int32_t, table, (size_t)i * 16 + 8);
			found = true;
			break;
		}
	if (!found || offset < 0)
		return buffMutationOriginal(mutator, model, info);
	void* equals_pair = (char*)klass + CLASS_VTABLE_OFFSET + (int64_t)offset * VIRTUAL_METHOD_SIZE;
	void* hash_pair =
	    (char*)klass + CLASS_VTABLE_OFFSET + (int64_t)(int32_t)((uint32_t)offset + 1) * VIRTUAL_METHOD_SIZE;
	if (FIELD(void*, equals_pair, 0) != buff_mutation.equals_method ||
	    FIELD(void*, hash_pair, 0) != buff_mutation.hash_method || !mutationContext(FIELD(void*, equals_pair, 8)) ||
	    !mutationContext(FIELD(void*, hash_pair, 8)))
		return buffMutationOriginal(mutator, model, info);
	uint64_t bucket_count = FIELD(uint64_t, buckets, ARRAY_LENGTH_OFFSET);
	void* entries = FIELD(void*, set, 0x18);
	if (!entries || bucket_count == 0 || bucket_count > INT32_MAX)
		return buffMutationOriginal(mutator, model, info);
	uint64_t entry_count = FIELD(uint64_t, entries, ARRAY_LENGTH_OFFSET);
	if (entry_count > INT32_MAX)
		return buffMutationOriginal(mutator, model, info);
	int32_t key = FIELD(int32_t, model, 0x14);
	uint32_t hash = (uint32_t)key & 0x7fffffff;
	int32_t index =
	    (int32_t)((uint32_t)(FIELD(int32_t, buckets, ARRAY_DATA_OFFSET + (size_t)(hash % (uint32_t)bucket_count) * 4)) -
	              1u);
	for (uint64_t steps = 0; index >= 0; ++steps)
	{
		if ((uint32_t)index >= entry_count || steps > entry_count)
			return buffMutationOriginal(mutator, model, info);
		void* entry = (char*)entries + ARRAY_DATA_OFFSET + (size_t)index * 12;

		if (FIELD(int32_t, entry, 0) == (int32_t)hash && FIELD(int32_t, entry, 8) == key)
		{
			return 1;
		}
		index = FIELD(int32_t, entry, 4);
	}

	return 0;
}

typedef struct GridPositionConfig
{
	void* getter;
	void* initialized;
	void* get_x;
	void* get_y;
} GridPositionConfig;

static GridPositionConfig grid_position;
API void ConfigureGridPosition(const GridPositionConfig* c)
{
	grid_position = *c;
}

API uint64_t CollisionGridPositionPair(void* object)
{
	void* klass = FIELD(void*, object, 0);
	void* method = FIELD(void*, klass, 0x828);
	void* transform = FIELD(void*, object, 0xa0);
	if (configured_thread && method == grid_position.getter && grid_position.initialized &&
	    FIELD(uint8_t, grid_position.initialized, 0) && transform)
	{
		void* position = FIELD(void*, transform, 0x58);
		if (position)
		{
			return (uint64_t)FIELD(uint32_t, position, 0x10) | ((uint64_t)FIELD(uint32_t, position, 0x14) << 32);
		}
	}

	void* first = ((void* (*)(void*, void*))method)(object, FIELD(void*, klass, 0x830));
	float x = ((float (*)(void*, void*))grid_position.get_x)(first, NULL);
	uint32_t x_bits;
	memcpy(&x_bits, &x, 4);
	// Reload after the first getter; overrides can replace the class, method or position.
	klass = FIELD(void*, object, 0);
	void* second = ((void* (*)(void*, void*))(FIELD(void*, klass, 0x828)))(object, FIELD(void*, klass, 0x830));
	float y = ((float (*)(void*, void*))grid_position.get_y)(second, NULL);
	uint32_t y_bits;
	memcpy(&y_bits, &y, 4);
	return (uint64_t)x_bits | ((uint64_t)y_bits << 32);
}

API void ClearCaches(void)
{
	memset(&position_binding, 0, sizeof(position_binding));
	for (CandidateBinding* binding = bloon_bindings; binding < bloon_bindings + 2; ++binding)
		memset(binding, 0, sizeof(*binding));
	clearBuffReuse();
	clearRoundBuffers();
	clearTextCache();
	clearSaveBuffers();
	clearDispatch();
	for (ComparerSlot* s = comparers; s < comparers + 1024; ++s)
	{
		if (s->handle)
			((HandleFree)config.handle_free)(s->handle);
		memset(s, 0, sizeof(*s));
	}
	for (PoolSlot *s = pool, *s_end = pool + pool_capacity; s < s_end; ++s)
		if (!s->busy)
		{
			if (s->handle)
				((HandleFree)config.handle_free)(s->handle);
			memset(s, 0, sizeof(*s));
		}
}
