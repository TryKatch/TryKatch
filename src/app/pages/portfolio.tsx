export const Portfolio = () => {
  return (
    <section className="w-[90%] mx-auto py-16 bg-gray-50">
      <div className="container mx-auto px-4 md:px-6 text-center">
        <h2 className="text-3xl md:text-4xl font-bold text-[#2a2a8e]">
          Our Work
        </h2>
        <p className="mt-4 text-gray-600 max-w-2xl mx-auto">
          Take a look at some of the amazing projects we have delivered.
        </p>
        {/* Placeholder for portfolio items */}
        <div className="mt-8 grid grid-cols-1 md:grid-cols-3 gap-6">
          <div className="bg-white shadow-lg rounded-lg p-6">Project 1</div>
          <div className="bg-white shadow-lg rounded-lg p-6">Project 2</div>
          <div className="bg-white shadow-lg rounded-lg p-6">Project 3</div>
        </div>
      </div>
    </section>
  );
};
