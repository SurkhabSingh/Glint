import ScanCard from "../components/ScanCard";

/** Ports the WinUI Search page: query box, summary, rich result cards. */
function SearchPage({ query, onQueryChange, onSearch, searchSummary, results }) {
  function onKeyDown(e) {
    if (e.key === "Enter") {
      e.preventDefault();
      onSearch();
    }
  }

  return (
    <div className="glint-page">
      <div className="glint-page-inner narrow">
        <h1 className="glint-title">Search</h1>
        <p className="glint-hint">
          Find a person, application, topic, summary, or phrase in your
          encrypted local context.
        </p>

        <div className="search-box-card">
          <input
            value={query}
            onChange={(e) => onQueryChange(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder="Search your memory"
            aria-label="Search your memory"
          />
          <button className="glint-btn primary" onClick={onSearch}>
            Search
          </button>
        </div>

        <p className="glint-section-sub">{searchSummary}</p>
        <div className="scan-list">
          {results.map((result) => (
            <ScanCard key={result.id} search={result} />
          ))}
        </div>
      </div>
    </div>
  );
}

export default SearchPage;
