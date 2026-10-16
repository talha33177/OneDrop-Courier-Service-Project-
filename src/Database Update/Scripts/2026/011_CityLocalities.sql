-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 011_CityLocalities.sql
-- Purpose: The neighbourhoods addresses actually name (Agargaon, Farmgate, Khilkhet, Khulshi...) in Dhaka city, its
--          suburbs and the other divisional cities, each an area of the zone its hub serves. 010 added the districts and
--          upazilas; this adds the places inside them. A name used twice, or already taken, gets its zone in brackets
--          because area names are unique. The courier's admin adds any place still missing on the Coverage page.
-- Author: Courier team
-- Date: 2026-10-16
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
DECLARE @Courier BIGINT = (SELECT Id FROM Platform.Tenant WHERE Slug = N'onedrop');

DECLARE @Places TABLE (ZoneCode NVARCHAR (20) NOT NULL, [Name] NVARCHAR (200) NOT NULL);

INSERT INTO @Places
    (ZoneCode, [Name])
SELECT
    P.ZoneCode,
    LTRIM(RTRIM(S.value))
FROM
    (VALUES
        (N'MIR', N'Mirpur 6, Mirpur 7, Mirpur 13, Mirpur 14, Mirpur DOHS, Kafrul, Ibrahimpur, Bhashantek, Kalshi, Senpara Parbata, Monipur, Rupnagar, Gabtoli, Darussalam, Technical, Kallyanpur, Mazar Road, Pirerbagh, Shah Ali Bagh, Bangla College'),
        (N'UTT', N'Uttara Sector 1, Uttara Sector 2, Uttara Sector 3, Uttara Sector 5, Uttara Sector 6, Uttara Sector 8, Uttara Sector 9, Uttara Sector 11, Uttara Sector 12, Uttara Sector 14, Uttara Sector 15, Uttara Sector 16, Uttara Sector 17, Uttara Sector 18, Khilkhet, Nikunja, Kuril, Kawla, Abdullahpur, Azampur, Baunia, Dakshinkhan, Uttarkhan, Turag, Haji Camp, Jashimuddin, Rajlakshmi, Faydabad, Bishwa Road'),
        (N'MOH', N'Agargaon, Sher-e-Bangla Nagar, Taltola, Asad Gate, Tajmahal Road, Japan Garden City, Bosila, Beribadh, Katasur, Shekhertek, Ring Road, Nurjahan Road, Baitul Aman Housing, Kaderabad Housing, Salimullah Road, Mohammadia Housing, Krishi Market'),
        (N'DHN', N'Dhanmondi 27, Dhanmondi 32, Satmasjid Road, Rayer Bazar, New Market, Elephant Road, Katabon, Nilkhet, Dhaka University, Panthapath, Green Road, Hatirpool, Azimpur, Palashi, Lake Circus, Mirpur Road, Sobhanbag, Rapa Plaza'),
        (N'GUL', N'Baridhara DOHS, Shahjadpur, Gulshan Avenue, Madani Avenue, Park Road, Gulshan Model Town'),
        (N'BAN', N'Mohakhali DOHS, Kakoli, Tejgaon Industrial Area, Tejkunipara, Farmgate, Kawran Bazar, Tejturi Bazar, Nakhalpara, Bijoy Sarani, Dhaka Cantonment, Kurmitola, Sonargaon Road, Amtoli, Jahangir Gate, Chairman Bari'),
        (N'BAD', N'Merul Badda, Middle Badda, North Badda, South Badda, Uttar Badda, Aftabnagar, Bashundhara R/A, Vatara, Nadda, Notun Bazar, Hatirjheel, Goran, Basabo, Sipahibag, Meradia, Khilgaon Taltola, Satarkul, Link Road, Joarsahara, East Rampura, West Rampura, DIT Road'),
        (N'MOT', N'Dilkusha, Fakirapool, Naya Paltan, Purana Paltan, Bijoynagar, Kakrail, Siddheshwari, Shantinagar, Segunbagicha, Ramna, Eskaton, Mouchak, Moghbazar, Mugda, Maniknagar, Shahjahanpur, Gopibagh, Sabujbagh, Mugdapara, Dayaganj, Rajarbagh, Shantibag, Sayedabad, Matuail, Konapara, Dholaipar, Shanir Akhra, Shahbagh, Bangla Motor, Press Club, Topkhana Road'),
        (N'OLD', N'Gandaria, Sutrapur, Tikatuli, Kotwali, Chawkbazar, Bangshal, Nawabpur, Islampur, Armanitola, Shankhari Bazar, Narinda, Faridabad, Bakshi Bazar, Nazimuddin Road, Babu Bazar, Mitford, Kamrangirchar, Shyampur, Dholaikhal, Postogola, Jurain, Kadamtali, Demra, Sarulia, Dania, Gulistan, Fulbaria, Urdu Road, Chankharpul, Siddique Bazar, Nazira Bazar'),
        (N'SAV', N'Savar Cantonment, Jahangirnagar University, Nabinagar, Baipail, Genda, Zirabo, Birulia, Dhamsona, Aminbazar, Tetuljhora, Bhakurta, Pathalia, Banagram, EPZ Savar, Savar Bus Stand, Radio Colony, Kathgora'),
        (N'GAZ', N'Joydebpur, Chowrasta, Konabari, Kashimpur, Mawna, Pubail, Rajendrapur, Gachha, Salna, Bhawal Mirzapur, Telirchala, Tongi Station Road'),
        (N'NAR', N'Chashara, Tanbazar, Kanchpur, Mograpara, Bhulta, Kanchan, Gognagar, Madanpur, Panchabati, Enayetnagar, Shimrail, Kutubpur, Godnail, Sanarpar, Nitaiganj, Kalirbazar, Pagla'),
        (N'KER', N'Aganagar, Ati Bazar, Kalatia, Taranagar, Hasnabad, Rohitpur, Basta, Konda, Tegharia, Subhadya'),
        (N'CTG', N'Kotwali, Pahartali, Khulshi, Double Mooring, Bakalia, Chandgaon, Bandar, Sholoshahar, Muradpur, Oxygen, Lalkhan Bazar, Jamal Khan, Andarkilla, Dampara, Firingee Bazar, Nandankanan, Probartak, CDA Avenue, Mehedibagh, Kazir Dewri, Pathantooly, Alankar, Kalurghat, Sagarika, Katghar, Akbarshah, Bahaddarhat, EPZ Chattogram, Rahattarpukur, Dewanhat, Sugandha, Wasa'),
        (N'SYL', N'Shibganj, Mirabazar, Tilagor, Subid Bazar, Kumarpara, Baghbari, Pathantula, Rikabi Bazar, Bandar Bazar, Naiorpul, Dargah Gate, Chowhatta, Mirboxtula, Jalalabad, Akhalia, Lamabazar'),
        (N'RAJ', N'Rajpara, Greater Road, Laxmipur, Kazla, Vodra, Sagarpara, Rajshahi Upashahar, Katakhali, Kashiadanga, Padma Abasik, Hetem Khan, Ghoramara, Talaimari, Binodpur, Alupatti, Sopura, Court Station'),
        (N'KHU', N'Boyra, Shiromoni, Rupsha, Gollamari, Fulbarigate, Majid Sarani, Nirala, Sher-e-Bangla Road, Moilapota, Dakbangla, Zilla School, Jorakal, Teligati, Tootpara, Mujgunni, Mirerdanga, Lobonchora'),
        (N'BAR', N'Band Road, Sagardi, Kashipur, College Road, Amtala, Launch Ghat, Bell''s Park, Sadar Road, Kotwali, Medical College, Girja Mahalla, Alekanda, Kalibari Road, Beltola, Chawkbazar, Notun Bazar'),
        (N'RNG', N'Jahaj Company More, Station Road, Lalbagh, Mahiganj, Pairaband, Nawabganj Bazar, Medical More, Munshipara, Satmatha, Guptapara, Kamal Kachna, Keranipara, Tajhat, Shalbon, Cadet College More'),
        (N'MYM', N'Ganginar Par, Town Hall, Kachijhuli, Akua, Maskanda, Kewatkhali, Brahmapura, Digarkanda, Bipin Park, Shashi Lodge, Ashtadhar, Mymensingh Medical College'),
        (N'CUM', N'Dhormopur, Rajganj, Jhawtala, Race Course, Kotbari, Padua Bazar, Suaganj, Monoharpur, Badurtala, Chawkbazar, Alekhar Char, Shashongacha, Jangalia, Notun Bazar')
    ) P (ZoneCode, Names)
    CROSS APPLY
        STRING_SPLIT(P.Names, N',') S
WHERE
    LTRIM(RTRIM(S.value)) <> N'';

INSERT INTO Network.Area
    (TenantId, ZoneId, [Name])
SELECT
    @Courier,
    Z.Id,
    CASE
        WHEN COUNT(*) OVER (PARTITION BY Pl.[Name]) > 1 OR
             EXISTS (SELECT 1 FROM Network.Area E WHERE E.TenantId = @Courier AND E.[Name] = Pl.[Name])
        THEN Pl.[Name] + N' (' + Z.[Name] + N')'
        ELSE Pl.[Name]
    END
FROM
    @Places Pl
    INNER JOIN
        Network.Zone Z
    ON Z.TenantId = @Courier AND
       Z.Code = Pl.ZoneCode
WHERE
    NOT EXISTS (SELECT 1 FROM Network.Area E WHERE E.TenantId = @Courier AND E.ZoneId = Z.Id AND E.[Name] = Pl.[Name]);
