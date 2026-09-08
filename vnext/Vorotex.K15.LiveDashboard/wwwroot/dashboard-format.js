globalThis.K15DashboardFormat={time(value){if(!value)return 'no timestamp';const date=new Date(value);return Number.isNaN(date.getTime())?'unknown time':date.toLocaleString();}};
