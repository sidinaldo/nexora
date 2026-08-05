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
