import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormularioDto } from '../../nucleo/modelos';
import { Formularios } from './formularios';

/** O CÓDIGO GERADO É PRODUTO.
 *
 *  Ele sai daqui e vai para o site do cliente, onde fica por anos. Ninguém vai revisá-lo lá.
 *  Se o campo-armadilha sumir num refactor, ou a chave não entrar na URL, o sintoma aparece
 *  semanas depois como "não chega lead" ou "chegou spam" — e não como um erro de build.
 *
 *  Estes testes montam o snippet e o EXECUTAM numa página de verdade, com `fetch` interceptado,
 *  para conferir o que ele realmente manda. */
describe('formulários do site', () => {
  const FORM: FormularioDto = {
    id: 7,
    nome: 'Página de contato',
    chave: 'a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718',
    dominioPermitido: 'www.cliente.com.br',
    ativo: true,
    leadsRecebidos: 12,
    criadoEm: '2026-08-01T10:00:00Z'
  };

  let componente: Formularios;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    const fixture = TestBed.createComponent(Formularios);
    componente = fixture.componentInstance;
    TestBed.inject(HttpTestingController).match(() => true).forEach(r => r.flush([]));
  });

  it('a chave NÃO aparece até alguém pedir para ver', () => {
    // Ela abre um endpoint de escrita na internet. Deixá-la impressa na lista é deixá-la num
    // monitor esquecido aberto — e em qualquer print da tela.
    expect(componente.estaRevelada(FORM.id)).toBeFalse();

    const mascarada = componente.mascarada(FORM.chave);
    expect(mascarada).not.toContain(FORM.chave);
    expect(mascarada.startsWith('a1b2')).withContext('dá para saber qual formulário é').toBeTrue();

    componente.revelar(FORM.id);
    expect(componente.estaRevelada(FORM.id)).toBeTrue();

    // Fechar o painel esconde de novo.
    componente.aberto.set(FORM.id);
    componente.alternarPainel(FORM.id);
    expect(componente.estaRevelada(FORM.id)).toBeFalse();
  });

  it('o HTML gerado leva a chave na URL e o campo-armadilha', () => {
    const html = componente.html(FORM);

    expect(html).toContain(`/captura/${FORM.chave}`);
    expect(html).toContain('name="telefone"');

    // ===== O CAMPO-ARMADILHA =====
    // Fora da tela, e NÃO por `display:none`: bot decente pula campo escondido por display e
    // preenche o que só está posicionado longe. `tabindex="-1"` e `aria-hidden` mantêm teclado e
    // leitor de tela fora dele.
    expect(html).toContain('name="website"');
    expect(html).toContain('tabindex="-1"');

    // A checagem do `display:none` é no DOM PARSEADO, não na string: o comentário do próprio
    // snippet diz "não troque por display:none", e procurar a literal no texto acusaria o aviso
    // em vez do estilo. O que importa é o `style` do elemento.
    const palco = document.createElement('div');
    palco.innerHTML = html;
    const armadilha = palco.querySelector('[name=website]') as HTMLElement;
    const caixa = armadilha.closest('[aria-hidden]') as HTMLElement;

    expect(caixa).withContext('a armadilha não está num bloco aria-hidden').not.toBeNull();
    expect(caixa.style.display).not.toBe('none');
    expect(caixa.style.left).toBe('-9999px');

    // Nenhuma dependência externa: o snippet é colado numa página estática qualquer.
    expect(html).not.toContain('src=');
  });

  it('o HTML gerado FUNCIONA colado numa página estática', async () => {
    // ===================== O TESTE QUE VALE =====================
    // Ler a string com regex não prova nada: o snippet pode conter tudo que se procura e ainda
    // assim não enviar. Aqui ele é INJETADO numa página de verdade, o script roda, o submit é
    // disparado e o `fetch` é interceptado — é o que o site do cliente vai fazer.
    // ============================================================
    const palco = document.createElement('div');
    document.body.appendChild(palco);

    const chamadas: { url: string; corpo: Record<string, unknown> }[] = [];
    const fetchOriginal = window.fetch;
    window.fetch = ((url: string, opcoes: RequestInit) => {
      chamadas.push({ url, corpo: JSON.parse(opcoes.body as string) });
      return Promise.resolve({
        ok: true,
        json: () => Promise.resolve({ recebido: true, mensagem: 'Recebemos seu contato.' })
      } as Response);
    }) as typeof window.fetch;

    try {
      // `innerHTML` não executa `<script>` — o navegador ignora script inserido assim. Recriar a
      // tag é o que o parser faria numa página carregada de verdade.
      const bruto = componente.html(FORM);
      palco.innerHTML = bruto;
      palco.querySelectorAll('script').forEach(velho => {
        const novo = document.createElement('script');
        novo.textContent = velho.textContent;
        velho.replaceWith(novo);
      });

      const form = palco.querySelector('#nexora-form') as HTMLFormElement;
      expect(form).withContext('o snippet não produziu um formulário').not.toBeNull();

      (form.querySelector('[name=nome]') as HTMLInputElement).value = 'Marcos Antunes';
      (form.querySelector('[name=telefone]') as HTMLInputElement).value = '(84) 98888-7777';
      (form.querySelector('[name=email]') as HTMLInputElement).value = 'marcos@exemplo.com';
      (form.querySelector('[name=mensagem]') as HTMLTextAreaElement).value = 'Quero um orçamento';

      form.dispatchEvent(new Event('submit', { cancelable: true, bubbles: true }));
      await Promise.resolve();

      expect(chamadas.length).withContext('o submit não chamou a API').toBe(1);
      expect(chamadas[0].url).toContain(`/captura/${FORM.chave}`);
      expect(chamadas[0].corpo['nome']).toBe('Marcos Antunes');
      expect(chamadas[0].corpo['telefone']).toBe('(84) 98888-7777');
      expect(chamadas[0].corpo['mensagem']).toBe('Quero um orçamento');

      // Armadilha vazia quando é gente preenchendo — o campo existe, mas ninguém o vê.
      expect(chamadas[0].corpo['armadilha']).toBe('');

      // O aviso avisa: sem ele, quem preencheu não sabe se deu certo e preenche de novo.
      // A cadeia do snippet tem dois `.then` encadeados — um único tick de microtarefa pega o
      // `fetch` mas ainda não a resposta. Uma macrotarefa drena tudo.
      await new Promise(pronto => setTimeout(pronto, 0));
      expect((palco.querySelector('#nexora-aviso') as HTMLElement).textContent)
        .toContain('Recebemos');

      // E o formulário volta ao zero: quem enviou não reenvia o mesmo texto sem perceber.
      expect((form.querySelector('[name=nome]') as HTMLInputElement).value).toBe('');
    } finally {
      window.fetch = fetchOriginal;
      palco.remove();
    }
  });

  it('o campo-armadilha preenchido viaja como `armadilha` — é o que o servidor descarta', async () => {
    const palco = document.createElement('div');
    document.body.appendChild(palco);

    const chamadas: Record<string, unknown>[] = [];
    const fetchOriginal = window.fetch;
    window.fetch = ((_url: string, opcoes: RequestInit) => {
      chamadas.push(JSON.parse(opcoes.body as string));
      return Promise.resolve({ ok: true, json: () => Promise.resolve({}) } as Response);
    }) as typeof window.fetch;

    try {
      palco.innerHTML = componente.html(FORM);
      palco.querySelectorAll('script').forEach(velho => {
        const novo = document.createElement('script');
        novo.textContent = velho.textContent;
        velho.replaceWith(novo);
      });

      const form = palco.querySelector('#nexora-form') as HTMLFormElement;
      (form.querySelector('[name=nome]') as HTMLInputElement).value = 'Bot Silva';
      (form.querySelector('[name=telefone]') as HTMLInputElement).value = '84988887777';
      // O bot preenche tudo que encontra, inclusive o que não vê.
      (form.querySelector('[name=website]') as HTMLInputElement).value = 'http://spam.example';

      form.dispatchEvent(new Event('submit', { cancelable: true, bubbles: true }));
      await Promise.resolve();

      expect(chamadas[0]['armadilha']).toBe('http://spam.example');
    } finally {
      window.fetch = fetchOriginal;
      palco.remove();
    }
  });

  // ==================================================================== o rastro (INT-4)
  /** Roda o snippet numa página de verdade e devolve o corpo que ele postou.
   *
   *  ⚠️ `history.replaceState` em vez de stub de `location`: o snippet lê `location.search`
   *  direto, e é exatamente isso que precisa ser exercitado. Trocar a URL da própria página de
   *  teste é o que mais se aproxima do que o navegador do visitante faz. */
  async function postarComRastro(opcoes: {
    query?: string;
    cookies?: string[];
    pixel?: boolean;
    antesDoEnvio?: () => void;
  }): Promise<{ corpo: Record<string, unknown>; eventosDoPixel: unknown[][] }> {
    const urlOriginal = location.pathname + location.search;
    const fetchOriginal = window.fetch;
    const janela = window as unknown as Record<string, unknown>;
    const fbqOriginal = janela['fbq'];

    const palco = document.createElement('div');
    document.body.appendChild(palco);

    const corpos: Record<string, unknown>[] = [];
    const eventosDoPixel: unknown[][] = [];

    window.fetch = ((_url: string, op: RequestInit) => {
      corpos.push(JSON.parse(op.body as string));
      return Promise.resolve({ ok: true, json: () => Promise.resolve({}) } as Response);
    }) as typeof window.fetch;

    if (opcoes.pixel) janela['fbq'] = (...args: unknown[]) => eventosDoPixel.push(args);

    try {
      history.replaceState({}, '', location.pathname + (opcoes.query ?? ''));
      for (const c of opcoes.cookies ?? []) document.cookie = c + '; path=/';

      palco.innerHTML = componente.html(FORM);
      palco.querySelectorAll('script').forEach(velho => {
        const novo = document.createElement('script');
        novo.textContent = velho.textContent;
        velho.replaceWith(novo);
      });

      const form = palco.querySelector('#nexora-form') as HTMLFormElement;
      (form.querySelector('[name=nome]') as HTMLInputElement).value = 'Bruna Lima';
      (form.querySelector('[name=telefone]') as HTMLInputElement).value = '84988887777';

      opcoes.antesDoEnvio?.();

      form.dispatchEvent(new Event('submit', { cancelable: true, bubbles: true }));
      await new Promise(pronto => setTimeout(pronto, 0));

      return { corpo: corpos[0], eventosDoPixel };
    } finally {
      window.fetch = fetchOriginal;
      if (opcoes.pixel) { if (fbqOriginal === undefined) delete janela['fbq']; else janela['fbq'] = fbqOriginal; }
      for (const c of opcoes.cookies ?? []) {
        document.cookie = c.split('=')[0] + '=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT';
      }
      history.replaceState({}, '', urlOriginal);
      palco.remove();
    }
  }

  it('O SNIPPET LEVA O RASTRO DO ANÚNCIO junto com o lead', async () => {
    const { corpo } = await postarComRastro({
      query: '?utm_source=instagram&utm_medium=cpc&utm_campaign=promo%20de%20marco'
           + '&fbclid=IwAR-do-clique&gclid=GCL-1&ttclid=TT-1',
      cookies: ['_fbp=fb.1.1700000000.111']
    });

    const r = corpo['rastreio'] as Record<string, string>;

    expect(r['utmSource']).toBe('instagram');
    expect(r['utmMedium']).toBe('cpc');
    // `%20` decodificado: a campanha com espaço tem de chegar legível, senão o relatório mostra
    // "promo%20de%20marco" e duas campanhas iguais viram duas linhas diferentes.
    expect(r['utmCampaign']).toBe('promo de marco');
    expect(r['fbclid']).toBe('IwAR-do-clique');
    expect(r['gclid']).toBe('GCL-1');
    expect(r['ttclid']).toBe('TT-1');
    expect(r['fbp']).toBe('fb.1.1700000000.111');
    // ⚠️ A PÁGINA VAI INTEIRA, com query string. Quem corta é o SERVIDOR
    // (`RegrasRastreio.SemQuery`) — uma cópia só da regra, e no lado que não dá para editar
    // colando HTML errado no site.
    expect(r['pagina']).toContain('context.html');
    expect(r['pagina']).toContain('utm_source=instagram');
    expect(r['eventoId']).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('SEM PIXEL NO SITE, o `fbc` é montado do `fbclid` — e é isso que faz funcionar sem nada instalado', async () => {
    // A Meta permite explicitamente: sem o cookie `_fbc`, monte `fb.{indice}.{ms}.{fbclid}`.
    // É o caminho do cliente que nunca instalou pixel, que é a maioria deles.
    const { corpo } = await postarComRastro({ query: '?fbclid=IwAR-sem-pixel' });

    const r = corpo['rastreio'] as Record<string, string>;
    expect(r['fbc']).toMatch(/^fb\.1\.\d{13}\.IwAR-sem-pixel$/);
    expect(r['fbp']).toBe('');
  });

  it('COM PIXEL, o cookie `_fbc` GANHA do montado', async () => {
    // O cookie é o que a própria Meta escreveu, com o índice de subdomínio e o instante certos.
    // Sobrescrevê-lo por um montado por nós seria trocar o dado bom pelo aproximado.
    const { corpo } = await postarComRastro({
      query: '?fbclid=IwAR-novo',
      cookies: ['_fbc=fb.2.1699999999.IwAR-original']
    });

    expect((corpo['rastreio'] as Record<string, string>)['fbc']).toBe('fb.2.1699999999.IwAR-original');
  });

  it('O RASTRO É LIDO NA CARGA DA PÁGINA, não no envio', async () => {
    // ⚠️ O TESTE QUE PROTEGE A REGRA MAIS FÁCIL DE PERDER. Num site de página única a URL muda a
    // cada navegação, e o `fbclid` desaparece dela antes de a pessoa clicar em Enviar. Se o
    // snippet lesse no `submit`, o rastro chegaria vazio justamente para quem veio de anúncio.
    const { corpo } = await postarComRastro({
      query: '?fbclid=IwAR-do-clique&utm_campaign=promo',
      antesDoEnvio: () => history.replaceState({}, '', location.pathname)
    });

    const r = corpo['rastreio'] as Record<string, string>;
    expect(r['fbclid']).toBe('IwAR-do-clique');
    expect(r['utmCampaign']).toBe('promo');
  });

  it('O MESMO `evento_id` VAI PARA A API E PARA O PIXEL — é o que impede contar o lead duas vezes', async () => {
    // Sem isto, o cliente com pixel instalado vê DOIS leads por pessoa: o do navegador e o do
    // servidor. O snippet dispara o evento do pixel sozinho, com o mesmo id — uma coisa a menos
    // para um cliente não técnico configurar errado.
    const { corpo, eventosDoPixel } = await postarComRastro({
      query: '?fbclid=IwAR-x',
      pixel: true
    });

    const esperado = (corpo['rastreio'] as Record<string, string>)['eventoId'];

    expect(eventosDoPixel.length).withContext('o pixel não foi avisado').toBe(1);
    expect(eventosDoPixel[0][0]).toBe('track');
    expect(eventosDoPixel[0][1]).toBe('Lead');
    expect(eventosDoPixel[0][3]).toEqual({ eventID: esperado });
  });

  it('SEM PIXEL NA PÁGINA nada estoura', async () => {
    // `typeof fbq === 'function'` e não `if (fbq)`: a segunda forma lança ReferenceError numa
    // página sem pixel, e o erro apareceria DEPOIS do envio — o lead entra, o aviso não aparece,
    // e o visitante preenche de novo.
    const { corpo, eventosDoPixel } = await postarComRastro({ query: '?utm_source=site' });

    expect(corpo['nome']).toBe('Bruna Lima');
    expect(eventosDoPixel.length).toBe(0);
  });

  it('o snippet de envio avulso aponta para a mesma URL e cita a armadilha', () => {
    const codigo = componente.fetch(FORM);
    expect(codigo).toContain(`/captura/${FORM.chave}`);
    expect(codigo).toContain('armadilha');
    expect(codigo).toContain("method: 'POST'");
  });

  it('o total soma os leads de todos os formulários', () => {
    componente.lista.set([
      { ...FORM, id: 1, leadsRecebidos: 12 },
      { ...FORM, id: 2, leadsRecebidos: 5, ativo: false },
      { ...FORM, id: 3, leadsRecebidos: 0 }
    ]);
    expect(componente.total()).toBe(17);
  });
});
