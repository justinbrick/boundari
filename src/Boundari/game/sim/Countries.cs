namespace Boundari.Sim;

/// <summary>
/// Country names with their flag codes (ISO 3166-1 alpha-2, matching the files in
/// res://ui/flags). Used to name bots until real nations exist.
/// </summary>
public static class Countries
{
    public static readonly (string Code, string Name)[] All =
    {
        ("af", "Afghanistan"), ("al", "Albania"), ("dz", "Algeria"), ("ad", "Andorra"), ("ao", "Angola"),
        ("ar", "Argentina"), ("am", "Armenia"), ("au", "Australia"), ("at", "Austria"), ("az", "Azerbaijan"),
        ("bs", "Bahamas"), ("bh", "Bahrain"), ("bd", "Bangladesh"), ("by", "Belarus"), ("be", "Belgium"),
        ("bz", "Belize"), ("bj", "Benin"), ("bt", "Bhutan"), ("bo", "Bolivia"), ("ba", "Bosnia"),
        ("bw", "Botswana"), ("br", "Brazil"), ("bn", "Brunei"), ("bg", "Bulgaria"), ("bf", "Burkina Faso"),
        ("bi", "Burundi"), ("kh", "Cambodia"), ("cm", "Cameroon"), ("ca", "Canada"), ("cv", "Cape Verde"),
        ("cf", "Central Africa"), ("td", "Chad"), ("cl", "Chile"), ("cn", "China"), ("co", "Colombia"),
        ("km", "Comoros"), ("cg", "Congo"), ("cr", "Costa Rica"), ("hr", "Croatia"), ("cu", "Cuba"),
        ("cy", "Cyprus"), ("cz", "Czechia"), ("dk", "Denmark"), ("dj", "Djibouti"), ("do", "Dominican Republic"),
        ("ec", "Ecuador"), ("eg", "Egypt"), ("sv", "El Salvador"), ("gq", "Equatorial Guinea"), ("er", "Eritrea"),
        ("ee", "Estonia"), ("et", "Ethiopia"), ("fj", "Fiji"), ("fi", "Finland"), ("fr", "France"),
        ("ga", "Gabon"), ("gm", "Gambia"), ("ge", "Georgia"), ("de", "Germany"), ("gh", "Ghana"),
        ("gr", "Greece"), ("gl", "Greenland"), ("gt", "Guatemala"), ("gn", "Guinea"), ("gy", "Guyana"),
        ("ht", "Haiti"), ("hn", "Honduras"), ("hu", "Hungary"), ("is", "Iceland"), ("in", "India"),
        ("id", "Indonesia"), ("ir", "Iran"), ("iq", "Iraq"), ("ie", "Ireland"), ("il", "Israel"),
        ("it", "Italy"), ("ci", "Ivory Coast"), ("jm", "Jamaica"), ("jp", "Japan"), ("jo", "Jordan"),
        ("kz", "Kazakhstan"), ("ke", "Kenya"), ("xk", "Kosovo"), ("kw", "Kuwait"), ("kg", "Kyrgyzstan"),
        ("la", "Laos"), ("lv", "Latvia"), ("lb", "Lebanon"), ("ls", "Lesotho"), ("lr", "Liberia"),
        ("ly", "Libya"), ("li", "Liechtenstein"), ("lt", "Lithuania"), ("lu", "Luxembourg"), ("mg", "Madagascar"),
        ("mw", "Malawi"), ("my", "Malaysia"), ("mv", "Maldives"), ("ml", "Mali"), ("mt", "Malta"),
        ("mr", "Mauritania"), ("mu", "Mauritius"), ("mx", "Mexico"), ("md", "Moldova"), ("mc", "Monaco"),
        ("mn", "Mongolia"), ("me", "Montenegro"), ("ma", "Morocco"), ("mz", "Mozambique"), ("mm", "Myanmar"),
        ("na", "Namibia"), ("np", "Nepal"), ("nl", "Netherlands"), ("nz", "New Zealand"), ("ni", "Nicaragua"),
        ("ne", "Niger"), ("ng", "Nigeria"), ("kp", "North Korea"), ("mk", "North Macedonia"), ("no", "Norway"),
        ("om", "Oman"), ("pk", "Pakistan"), ("pa", "Panama"), ("pg", "Papua New Guinea"), ("py", "Paraguay"),
        ("pe", "Peru"), ("ph", "Philippines"), ("pl", "Poland"), ("pt", "Portugal"), ("qa", "Qatar"),
        ("ro", "Romania"), ("ru", "Russia"), ("rw", "Rwanda"), ("sa", "Saudi Arabia"), ("sn", "Senegal"),
        ("rs", "Serbia"), ("sl", "Sierra Leone"), ("sg", "Singapore"), ("sk", "Slovakia"), ("si", "Slovenia"),
        ("so", "Somalia"), ("za", "South Africa"), ("kr", "South Korea"), ("ss", "South Sudan"), ("es", "Spain"),
        ("lk", "Sri Lanka"), ("sd", "Sudan"), ("sr", "Suriname"), ("se", "Sweden"), ("ch", "Switzerland"),
        ("sy", "Syria"), ("tw", "Taiwan"), ("tj", "Tajikistan"), ("tz", "Tanzania"), ("th", "Thailand"),
        ("tg", "Togo"), ("tn", "Tunisia"), ("tr", "Turkey"), ("tm", "Turkmenistan"), ("ug", "Uganda"),
        ("ua", "Ukraine"), ("ae", "Emirates"), ("gb", "United Kingdom"), ("us", "United States"), ("uy", "Uruguay"),
        ("uz", "Uzbekistan"), ("va", "Vatican"), ("ve", "Venezuela"), ("vn", "Vietnam"), ("ye", "Yemen"),
        ("zm", "Zambia"), ("zw", "Zimbabwe"),
    };
}
