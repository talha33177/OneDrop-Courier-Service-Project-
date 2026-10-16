-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 010_BangladeshPlaces.sql
-- Purpose: Every place a parcel can be addressed to, for the address picker (division, zone, area). Each existing zone is
--          filed under its division; the other districts of Bangladesh become zones of their own (city = the district,
--          so a parcel to another district is charged the outside-city rate) served by the nearest hub, and every
--          upazila becomes an area of its district. An upazila whose name is used elsewhere gets its district in
--          brackets, because area names are unique. The courier's admin changes any of it on the Coverage page.
-- Author: Courier team
-- Date: 2026-10-16
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
DECLARE @Courier BIGINT = (SELECT Id FROM Platform.Tenant WHERE Slug = N'onedrop');

-- The zones the launch map already has, filed under their divisions
UPDATE Z
SET
    Division = D.Division,
    UpdatedOn = GETUTCDATE()
FROM
    Network.Zone Z
    INNER JOIN
        (VALUES
            (N'MIR', N'Dhaka'), (N'UTT', N'Dhaka'), (N'MOH', N'Dhaka'), (N'DHN', N'Dhaka'), (N'GUL', N'Dhaka'),
            (N'BAN', N'Dhaka'), (N'BAD', N'Dhaka'), (N'MOT', N'Dhaka'), (N'OLD', N'Dhaka'), (N'SAV', N'Dhaka'),
            (N'GAZ', N'Dhaka'), (N'NAR', N'Dhaka'), (N'KER', N'Dhaka'), (N'CTG', N'Chattogram'), (N'CUM', N'Chattogram'),
            (N'SYL', N'Sylhet'), (N'RAJ', N'Rajshahi'), (N'KHU', N'Khulna'), (N'BAR', N'Barishal'), (N'RNG', N'Rangpur'),
            (N'MYM', N'Mymensingh')
        ) D (Code, Division)
    ON Z.Code = D.Code
WHERE
    Z.TenantId = @Courier;

-- The other districts, each a zone served by the nearest hub
INSERT INTO Network.Zone
    (TenantId, Code, [Name], HubId, City, IsSuburb, Division)
SELECT
    @Courier,
    D.Code,
    D.[Name],
    H.Id,
    D.[Name],
    0,
    D.Division
FROM
    (VALUES
        (N'FARIDPUR',        N'Faridpur',        N'Dhaka',      N'MOT'),
        (N'GOPALGANJ',       N'Gopalganj',       N'Dhaka',      N'MOT'),
        (N'KISHOREGANJ',     N'Kishoreganj',     N'Dhaka',      N'GAZ'),
        (N'MADARIPUR',       N'Madaripur',       N'Dhaka',      N'MOT'),
        (N'MANIKGANJ',       N'Manikganj',       N'Dhaka',      N'SAV'),
        (N'MUNSHIGANJ',      N'Munshiganj',      N'Dhaka',      N'NAR'),
        (N'NARSINGDI',       N'Narsingdi',       N'Dhaka',      N'GAZ'),
        (N'RAJBARI',         N'Rajbari',         N'Dhaka',      N'MOT'),
        (N'SHARIATPUR',      N'Shariatpur',      N'Dhaka',      N'MOT'),
        (N'TANGAIL',         N'Tangail',         N'Dhaka',      N'SAV'),
        (N'BANDARBAN',       N'Bandarban',       N'Chattogram', N'CTG'),
        (N'BRAHMANBARIA',    N'Brahmanbaria',    N'Chattogram', N'CUM'),
        (N'CHANDPUR',        N'Chandpur',        N'Chattogram', N'CUM'),
        (N'COXSBAZAR',       N'Cox''s Bazar',    N'Chattogram', N'CTG'),
        (N'FENI',            N'Feni',            N'Chattogram', N'CUM'),
        (N'KHAGRACHHARI',    N'Khagrachhari',    N'Chattogram', N'CTG'),
        (N'LAKSHMIPUR',      N'Lakshmipur',      N'Chattogram', N'CUM'),
        (N'NOAKHALI',        N'Noakhali',        N'Chattogram', N'CUM'),
        (N'RANGAMATI',       N'Rangamati',       N'Chattogram', N'CTG'),
        (N'BOGURA',          N'Bogura',          N'Rajshahi',   N'RAJ'),
        (N'CHAPAINAWABGANJ', N'Chapainawabganj', N'Rajshahi',   N'RAJ'),
        (N'JOYPURHAT',       N'Joypurhat',       N'Rajshahi',   N'RAJ'),
        (N'NAOGAON',         N'Naogaon',         N'Rajshahi',   N'RAJ'),
        (N'NATORE',          N'Natore',          N'Rajshahi',   N'RAJ'),
        (N'PABNA',           N'Pabna',           N'Rajshahi',   N'RAJ'),
        (N'SIRAJGANJ',       N'Sirajganj',       N'Rajshahi',   N'RAJ'),
        (N'BAGERHAT',        N'Bagerhat',        N'Khulna',     N'KHU'),
        (N'CHUADANGA',       N'Chuadanga',       N'Khulna',     N'KHU'),
        (N'JASHORE',         N'Jashore',         N'Khulna',     N'KHU'),
        (N'JHENAIDAH',       N'Jhenaidah',       N'Khulna',     N'KHU'),
        (N'KUSHTIA',         N'Kushtia',         N'Khulna',     N'KHU'),
        (N'MAGURA',          N'Magura',          N'Khulna',     N'KHU'),
        (N'MEHERPUR',        N'Meherpur',        N'Khulna',     N'KHU'),
        (N'NARAIL',          N'Narail',          N'Khulna',     N'KHU'),
        (N'SATKHIRA',        N'Satkhira',        N'Khulna',     N'KHU'),
        (N'BARGUNA',         N'Barguna',         N'Barishal',   N'BAR'),
        (N'BHOLA',           N'Bhola',           N'Barishal',   N'BAR'),
        (N'JHALOKATI',       N'Jhalokati',       N'Barishal',   N'BAR'),
        (N'PATUAKHALI',      N'Patuakhali',      N'Barishal',   N'BAR'),
        (N'PIROJPUR',        N'Pirojpur',        N'Barishal',   N'BAR'),
        (N'HABIGANJ',        N'Habiganj',        N'Sylhet',     N'SYL'),
        (N'MOULVIBAZAR',     N'Moulvibazar',     N'Sylhet',     N'SYL'),
        (N'SUNAMGANJ',       N'Sunamganj',       N'Sylhet',     N'SYL'),
        (N'DINAJPUR',        N'Dinajpur',        N'Rangpur',    N'RNG'),
        (N'GAIBANDHA',       N'Gaibandha',       N'Rangpur',    N'RNG'),
        (N'KURIGRAM',        N'Kurigram',        N'Rangpur',    N'RNG'),
        (N'LALMONIRHAT',     N'Lalmonirhat',     N'Rangpur',    N'RNG'),
        (N'NILPHAMARI',      N'Nilphamari',      N'Rangpur',    N'RNG'),
        (N'PANCHAGARH',      N'Panchagarh',      N'Rangpur',    N'RNG'),
        (N'THAKURGAON',      N'Thakurgaon',      N'Rangpur',    N'RNG'),
        (N'JAMALPUR',        N'Jamalpur',        N'Mymensingh', N'MYM'),
        (N'NETROKONA',       N'Netrokona',       N'Mymensingh', N'MYM'),
        (N'SHERPUR',         N'Sherpur',         N'Mymensingh', N'MYM')
    ) D (Code, [Name], Division, HubCode)
    INNER JOIN
        Network.Hub H
    ON H.TenantId = @Courier AND
       H.Code = D.HubCode
WHERE
    NOT EXISTS (SELECT 1 FROM Network.Zone E WHERE E.TenantId = @Courier AND E.Code = D.Code);

-- Every upazila of every district (the launch zones keep the areas they have and gain the rest of their district)
DECLARE @Places TABLE (ZoneCode NVARCHAR (20) NOT NULL, [Name] NVARCHAR (200) NOT NULL);

INSERT INTO @Places
    (ZoneCode, [Name])
SELECT
    P.ZoneCode,
    LTRIM(RTRIM(S.value))
FROM
    (VALUES
        (N'SAV',             N'Dhamrai'),
        (N'KER',             N'Dohar, Nawabganj (Dhaka)'),
        (N'GAZ',             N'Kaliakair, Kapasia, Kaliganj, Sreepur'),
        (N'NAR',             N'Araihazar, Bandar, Rupganj, Sonargaon'),
        (N'FARIDPUR',        N'Faridpur Sadar, Alfadanga, Bhanga, Boalmari, Charbhadrasan, Madhukhali, Nagarkanda, Sadarpur, Saltha'),
        (N'GOPALGANJ',       N'Gopalganj Sadar, Kashiani, Kotalipara, Muksudpur, Tungipara'),
        (N'KISHOREGANJ',     N'Kishoreganj Sadar, Austagram, Bajitpur, Bhairab, Hossainpur, Itna, Karimganj, Katiadi, Kuliarchar, Mithamain, Nikli, Pakundia, Tarail'),
        (N'MADARIPUR',       N'Madaripur Sadar, Dasar, Kalkini, Rajoir, Shibchar'),
        (N'MANIKGANJ',       N'Manikganj Sadar, Daulatpur, Ghior, Harirampur, Saturia, Shivalaya, Singair'),
        (N'MUNSHIGANJ',      N'Munshiganj Sadar, Gazaria, Lohajang, Sirajdikhan, Sreenagar, Tongibari'),
        (N'NARSINGDI',       N'Narsingdi Sadar, Belabo, Monohardi, Palash, Raipura, Shibpur'),
        (N'RAJBARI',         N'Rajbari Sadar, Baliakandi, Goalanda, Kalukhali, Pangsha'),
        (N'SHARIATPUR',      N'Shariatpur Sadar, Bhedarganj, Damudya, Gosairhat, Naria, Zanjira'),
        (N'TANGAIL',         N'Tangail Sadar, Basail, Bhuapur, Delduar, Dhanbari, Ghatail, Gopalpur, Kalihati, Madhupur, Mirzapur, Nagarpur, Sakhipur'),
        (N'CTG',             N'Anwara, Banshkhali, Boalkhali, Chandanaish, Fatikchhari, Hathazari, Karnaphuli, Lohagara, Mirsharai, Patiya, Rangunia, Raozan, Sandwip, Satkania, Sitakunda'),
        (N'BANDARBAN',       N'Bandarban Sadar, Alikadam, Lama, Naikhongchhari, Rowangchhari, Ruma, Thanchi'),
        (N'BRAHMANBARIA',    N'Brahmanbaria Sadar, Akhaura, Ashuganj, Bancharampur, Bijoynagar, Kasba, Nabinagar, Nasirnagar, Sarail'),
        (N'CHANDPUR',        N'Chandpur Sadar, Faridganj, Haimchar, Haziganj, Kachua, Matlab Dakshin, Matlab Uttar, Shahrasti'),
        (N'COXSBAZAR',       N'Cox''s Bazar Sadar, Chakaria, Eidgaon, Kutubdia, Maheshkhali, Pekua, Ramu, Teknaf, Ukhia'),
        (N'CUM',             N'Barura, Brahmanpara, Burichang, Chandina, Chauddagram, Daudkandi, Debidwar, Homna, Laksam, Manoharganj, Meghna, Muradnagar, Nangalkot, Titas, Cumilla Sadar Dakshin'),
        (N'FENI',            N'Feni Sadar, Chhagalnaiya, Daganbhuiyan, Fulgazi, Parshuram, Sonagazi'),
        (N'KHAGRACHHARI',    N'Khagrachhari Sadar, Dighinala, Guimara, Lakshmichhari, Mahalchhari, Manikchhari, Matiranga, Panchhari, Ramgarh'),
        (N'LAKSHMIPUR',      N'Lakshmipur Sadar, Kamalnagar, Raipur, Ramganj, Ramgati'),
        (N'NOAKHALI',        N'Noakhali Sadar, Begumganj, Chatkhil, Companiganj, Hatiya, Kabirhat, Senbagh, Sonaimuri, Subarnachar'),
        (N'RANGAMATI',       N'Rangamati Sadar, Baghaichhari, Barkal, Belaichhari, Juraichhari, Kaptai, Kawkhali, Langadu, Naniarchar, Rajasthali'),
        (N'SYL',             N'Balaganj, Beanibazar, Bishwanath, Companiganj, Dakshin Surma, Fenchuganj, Golapganj, Gowainghat, Jaintiapur, Kanaighat, Osmani Nagar, Zakiganj'),
        (N'HABIGANJ',        N'Habiganj Sadar, Ajmiriganj, Bahubal, Baniachong, Chunarughat, Lakhai, Madhabpur, Nabiganj, Shayestaganj'),
        (N'MOULVIBAZAR',     N'Moulvibazar Sadar, Barlekha, Juri, Kamalganj, Kulaura, Rajnagar, Sreemangal'),
        (N'SUNAMGANJ',       N'Sunamganj Sadar, Bishwamvarpur, Chhatak, Dakshin Sunamganj, Derai, Dharampasha, Dowarabazar, Jagannathpur, Jamalganj, Madhyanagar, Sullah, Tahirpur'),
        (N'RAJ',             N'Bagha, Bagmara, Charghat, Durgapur, Godagari, Mohanpur, Paba, Puthia, Tanore'),
        (N'BOGURA',          N'Bogura Sadar, Adamdighi, Dhunat, Dhupchanchia, Gabtali, Kahaloo, Nandigram, Sariakandi, Shajahanpur, Sherpur (Bogura), Shibganj, Sonatala'),
        (N'CHAPAINAWABGANJ', N'Chapainawabganj Sadar, Bholahat, Gomastapur, Nachole, Shibganj'),
        (N'JOYPURHAT',       N'Joypurhat Sadar, Akkelpur, Kalai, Khetlal, Panchbibi'),
        (N'NAOGAON',         N'Naogaon Sadar, Atrai, Badalgachhi, Dhamoirhat, Manda, Mohadevpur, Niamatpur, Patnitala, Porsha, Raninagar, Sapahar'),
        (N'NATORE',          N'Natore Sadar, Bagatipara, Baraigram, Gurudaspur, Lalpur, Naldanga, Singra'),
        (N'PABNA',           N'Pabna Sadar, Atgharia, Bera, Bhangura, Chatmohar, Faridpur (Pabna), Ishwardi, Santhia, Sujanagar'),
        (N'SIRAJGANJ',       N'Sirajganj Sadar, Belkuchi, Chauhali, Kamarkhanda, Kazipur, Raiganj, Shahjadpur, Tarash, Ullahpara'),
        (N'KHU',             N'Batiaghata, Dacope, Dighalia, Dumuria, Koyra, Paikgachha, Phultala, Rupsa, Terokhada'),
        (N'BAGERHAT',        N'Bagerhat Sadar, Chitalmari, Fakirhat, Kachua, Mollahat, Mongla, Morrelganj, Rampal, Sarankhola'),
        (N'CHUADANGA',       N'Chuadanga Sadar, Alamdanga, Damurhuda, Jibannagar'),
        (N'JASHORE',         N'Jashore Sadar, Abhaynagar, Bagherpara, Chaugachha, Jhikargachha, Keshabpur, Manirampur, Sharsha'),
        (N'JHENAIDAH',       N'Jhenaidah Sadar, Harinakunda, Kaliganj, Kotchandpur, Maheshpur, Shailkupa'),
        (N'KUSHTIA',         N'Kushtia Sadar, Bheramara, Daulatpur, Khoksa, Kumarkhali, Mirpur (Kushtia)'),
        (N'MAGURA',          N'Magura Sadar, Mohammadpur, Shalikha, Sreepur'),
        (N'MEHERPUR',        N'Meherpur Sadar, Gangni, Mujibnagar'),
        (N'NARAIL',          N'Narail Sadar, Kalia, Lohagara'),
        (N'SATKHIRA',        N'Satkhira Sadar, Assasuni, Debhata, Kalaroa, Kaliganj, Shyamnagar, Tala'),
        (N'BAR',             N'Agailjhara, Babuganj, Bakerganj, Banaripara, Gaurnadi, Hizla, Mehendiganj, Muladi, Wazirpur'),
        (N'BARGUNA',         N'Barguna Sadar, Amtali, Bamna, Betagi, Patharghata, Taltali'),
        (N'BHOLA',           N'Bhola Sadar, Burhanuddin, Char Fasson, Daulatkhan, Lalmohan, Manpura, Tazumuddin'),
        (N'JHALOKATI',       N'Jhalokati Sadar, Kathalia, Nalchity, Rajapur'),
        (N'PATUAKHALI',      N'Patuakhali Sadar, Bauphal, Dashmina, Dumki, Galachipa, Kalapara, Mirzaganj, Rangabali'),
        (N'PIROJPUR',        N'Pirojpur Sadar, Bhandaria, Kawkhali, Mathbaria, Nazirpur, Nesarabad, Zianagar'),
        (N'RNG',             N'Badarganj, Gangachara, Kaunia, Mithapukur, Pirgachha, Pirganj, Taraganj'),
        (N'DINAJPUR',        N'Dinajpur Sadar, Birampur, Birganj, Biral, Bochaganj, Chirirbandar, Ghoraghat, Hakimpur, Kaharole, Khansama, Nawabganj (Dinajpur), Parbatipur, Phulbari'),
        (N'GAIBANDHA',       N'Gaibandha Sadar, Fulchhari, Gobindaganj, Palashbari, Sadullapur, Saghata, Sundarganj'),
        (N'KURIGRAM',        N'Kurigram Sadar, Bhurungamari, Char Rajibpur, Chilmari, Nageshwari, Phulbari, Rajarhat, Raomari, Ulipur'),
        (N'LALMONIRHAT',     N'Lalmonirhat Sadar, Aditmari, Hatibandha, Kaliganj, Patgram'),
        (N'NILPHAMARI',      N'Nilphamari Sadar, Dimla, Domar, Jaldhaka, Kishoreganj (Nilphamari), Saidpur'),
        (N'PANCHAGARH',      N'Panchagarh Sadar, Atwari, Boda, Debiganj, Tentulia'),
        (N'THAKURGAON',      N'Thakurgaon Sadar, Baliadangi, Haripur, Pirganj, Ranisankail'),
        (N'MYM',             N'Bhaluka, Dhobaura, Fulbaria, Gaffargaon, Gauripur, Haluaghat, Ishwarganj, Muktagachha, Nandail, Phulpur, Tarakanda, Trishal'),
        (N'JAMALPUR',        N'Jamalpur Sadar, Bakshiganj, Dewanganj, Islampur, Madarganj, Melandaha, Sarishabari'),
        (N'NETROKONA',       N'Netrokona Sadar, Atpara, Barhatta, Durgapur, Kalmakanda, Kendua, Khaliajuri, Madan, Mohanganj, Purbadhala'),
        (N'SHERPUR',         N'Sherpur Sadar, Jhenaigati, Nakla, Nalitabari, Sreebardi')
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
