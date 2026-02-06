using System.Threading.Tasks;

namespace GameLauncher.Data
{
    public class DatabaseInitializer
    {
        private readonly DatabaseContext _context;

        public DatabaseInitializer(DatabaseContext context)
        {
            _context = context;
        }

        public async Task InitializeAsync()
        {
            await _context.InitializeAsync();
        }
    }
}