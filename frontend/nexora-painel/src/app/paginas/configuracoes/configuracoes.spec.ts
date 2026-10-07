import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';
import { ConfiguracaoEmpresa } from '../../nucleo/modelos';
import { Configuracoes } from './configuracoes';

/** ===================== O LIGA/DESLIGA DA CONCLUSÃO DA VENDA (POS-1) =====================
 *
 *  Esta tela não tinha teste nenhum, e este arquivo não tenta cobri-la: cobre o controle que o
 *  POS-1 acrescentou, que é o único desta tela capaz de desfazer uma decisão do dono em silêncio.
 *
 *  As duas coisas que podem quebrar calado:
 *
 *    1. o campo de dias desaparecer ou PERDER O VALOR quando a chave é desligada. O número é
 *       guardado de propósito — é o que faz religar devolver o prazo que a empresa tinha, em vez
 *       de 7 por acidente;
 *
 *    2. o PUT sair sem o campo. O `CorpoAtendimento` o tem como obrigatório, então omitir não
 *       compila — mas mandar um literal em vez do sinal compila, e o dono salvaria a tela toda
 *       vendo o valor certo na frente dele e outro no banco.
 *  ====================================================================================== */
describe('configurações — a conclusão da venda liga e desliga', () => {
  let fixture: ComponentFixture<Configuracoes>;
  let c: Configuracoes;
  let http: HttpTestingController;

  const CONFIG: ConfiguracaoEmpresa = {
    nome: 'Softio', documento: null, fusoHorario: 'America/Sao_Paulo', uf: 'RN',
    janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126,
    semaforoAmareloMinutos: 60, semaforoVermelhoMinutos: 240,
    diasSemRespostaFollowUp: 2,
    // 30 e não 7: um valor que a empresa escolheu, para o teste poder provar que ele sobrevive.
    diasParaConcluirVenda: 30,
    conclusaoAutomatica: true,
    // A pesquisa LIGADA na fixture, com valores que a empresa escolheu — para o teste poder provar
    // que eles chegam na tela e voltam no PUT. O padrão de verdade é desligada.
    npsAtivo: true,
    npsDiasAposConclusao: 5,
    npsDiasExpiracao: 2,
    npsTexto: '{{saudacao}} Aqui é da {{empresa}}. De 0 a 10?',
    npsMensagemPromotor: 'Valeu!',
    npsMensagemDetrator: null
  };

  function montar(sobrepor: Partial<ConfiguracaoEmpresa> = {}) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideRouter([]),
        provideHttpClient(), provideHttpClientTesting()
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: {
        id: 1, nome: 'Dona', email: 'd@x.com', papel: 'dono',
        permissoes: PERMISSOES_DE.dono, empresaNome: 'Softio'
      }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Configuracoes);
    c = fixture.componentInstance;
    fixture.detectChanges();

    // A tela dispara quatro leituras no `ngOnInit`; só a primeira interessa aqui, e as outras
    // falham de propósito sem derrubar nada (é o comportamento declarado do componente).
    for (const r of http.match(() => true)) {
      if (r.request.url.endsWith('/configuracao')) r.flush({ ...CONFIG, ...sobrepor });
      else r.flush([]);
    }
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => http.verify());

  const campoDias = () =>
    fixture.nativeElement.querySelector('#cv') as HTMLInputElement;

  it('carrega LIGADO e com o campo de dias editável', () => {
    montar();

    expect(c.fConclusaoAuto()).toBeTrue();
    expect(c.fDiasConcluir()).toBe(30);
    expect(campoDias().disabled).toBeFalse();
  });

  it('carrega DESLIGADO quando é isso que o servidor diz', async () => {
    // ⚠️ ESTE TESTE EXISTE PORQUE O DE CIMA NÃO PROVA NADA SOZINHO. O sinal nasce `true`, e a
    // configuração padrão também é `true` — então um `carregar()` que simplesmente IGNORASSE a
    // resposta do servidor passaria. Descobri sabotando: apaguei a linha que lê o campo e nenhum
    // teste caiu.
    //
    // Uma empresa que desligou a conclusão abriria a tela vendo o interruptor ligado, e o primeiro
    // "salvar" de qualquer outro campo religaria a feature em silêncio.
    montar({ conclusaoAutomatica: false });
    await fixture.whenStable();

    expect(c.fConclusaoAuto()).toBeFalse();
    expect(campoDias().disabled).toBeTrue();
    expect(fixture.nativeElement.textContent).toContain('Nada é concluído sozinho');
  });

  it('DESLIGAR desabilita o campo de dias e MANTÉM o número', async () => {
    montar();

    c.fConclusaoAuto.set(false);
    fixture.detectChanges();
    // ⚠️ `[disabled]` é escrito no DOM por microtask, não no `detectChanges`. Sem este `await` o
    // teste lê o DOM velho e passa por acidente — já aconteceu neste projeto.
    await fixture.whenStable();

    expect(campoDias().disabled).withContext('o campo fica travado').toBeTrue();
    expect(c.fDiasConcluir()).withContext('mas o valor NÃO se perde').toBe(30);
    expect(campoDias()).withContext('e o campo continua na tela').not.toBeNull();
  });

  it('o aviso troca: desligado, diz que o card fica no quadro e segura a vaga do funil', async () => {
    montar();

    expect(fixture.nativeElement.textContent).toContain('o relógio para');

    c.fConclusaoAuto.set(false);
    fixture.detectChanges();
    await fixture.whenStable();

    const texto = fixture.nativeElement.textContent as string;
    expect(texto).toContain('Nada é concluído sozinho');
    // ⚠️ ESTA FRASE É O PREÇO HONESTO DE DESLIGAR, e esta é a única tela onde alguém o escolhe.
    // Sem ela, o sintoma é o funil deixar de aparecer na hora de abrir um negócio novo, sem
    // nenhuma explicação em lugar nenhum.
    expect(texto).toContain('não pode abrir outro negócio neste funil');
  });

  it('o PUT de atendimento manda a conclusão automática, com o valor da tela', () => {
    montar();

    c.fConclusaoAuto.set(false);
    c.salvarAtendimento();

    const req = http.expectOne(r => r.url.endsWith('/configuracao/atendimento'));

    expect(req.request.body.conclusaoAutomatica)
      .withContext('o valor da tela, não um literal').toBeFalse();
    expect(req.request.body.diasParaConcluirVenda)
      .withContext('e o prazo vai junto, mesmo desligado').toBe(30);

    req.flush(null);
    // O componente recarrega depois de salvar.
    for (const r of http.match(() => true)) {
      if (r.request.url.endsWith('/configuracao')) r.flush(CONFIG);
      else r.flush([]);
    }
  });

  // ==================================================================== pesquisa pós-venda (NPS-1)

  /** Drena as leituras que o `carregar()` refaz depois de salvar — o `http.verify()` do `afterEach`
   *  cobra cada uma. Mesmo laço que o teste do atendimento já usava no fim. */
  function drenarRecarga() {
    for (const r of http.match(() => true)) {
      if (r.request.url.endsWith('/configuracao')) r.flush(CONFIG);
      else r.flush([]);
    }
    fixture.detectChanges();
  }

  it('A CONFIGURAÇÃO DA PESQUISA CHEGA NA TELA E VOLTA NO PUT', () => {
    montar();

    expect(c.fNpsAtivo()).toBeTrue();
    expect(c.fNpsDias()).toBe(5);
    expect(c.fNpsExpiracao()).toBe(2);
    expect(c.fNpsPromotor()).toBe('Valeu!');
    // Nulo chega como string vazia: o `<textarea>` não aceita nulo.
    expect(c.fNpsDetrator()).toBe('');

    c.salvarPesquisaNps();

    const req = http.expectOne(r => r.url.endsWith('/configuracao/pesquisa-nps'));
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({
      npsAtivo: true,
      npsDiasAposConclusao: 5,
      npsDiasExpiracao: 2,
      npsTexto: '{{saudacao}} Aqui é da {{empresa}}. De 0 a 10?',
      npsMensagemPromotor: 'Valeu!',
      // ⚠️ VAZIO VIRA NULO: os dois querem dizer "não envia", e string em branco deixaria o campo
      // dizendo "há uma mensagem" sem mensagem nenhuma.
      npsMensagemDetrator: null
    });

    req.flush(null);
    drenarRecarga();
  });

  /** ===================== O `npsAtivo` VAI SEMPRE, MESMO FALSO =====================
   *
   *  ⚠️ A API o recebe ANULÁVEL e RECUSA nulo, e a razão é esta: omitido no corpo, ele chegaria
   *  como `false` — um valor VÁLIDO, que desligaria a pesquisa por uma escolha que ninguém fez.
   *  ============================================================================= */
  it('DESLIGAR A PESQUISA MANDA FALSE, E NÃO OMITE O CAMPO', () => {
    montar();

    c.fNpsAtivo.set(false);
    c.salvarPesquisaNps();

    const req = http.expectOne(r => r.url.endsWith('/configuracao/pesquisa-nps'));

    expect(Object.keys(req.request.body as object)).toContain('npsAtivo');
    expect((req.request.body as { npsAtivo: boolean }).npsAtivo).toBeFalse();

    req.flush(null);
    drenarRecarga();
  });

  /** ===================== A PRÉ-VISUALIZAÇÃO MOSTRA O CASO RUIM =====================
   *
   *  ⚠️ É O PONTO TODO DELA. Quem escreve `"Oi, {{nome}}!"` vê a prévia boa e salva; só a prévia
   *  SEM nome revela "Oi, !" — que é o que recebe o cliente cujo WhatsApp não manda o nome do
   *  perfil. `NomeDePessoa` documenta esse defeito tendo acontecido de verdade, com "(84)" no
   *  lugar do nome.
   *  ============================================================================== */
  it('A PRÉ-VISUALIZAÇÃO REVELA O "Oi, !" DE QUEM NÃO TEM NOME', () => {
    montar();

    c.fNpsTexto.set('Oi, {{nome}}! Aqui é da {{empresa}}.');

    expect(c.previaComNome()).toBe('Oi, Maria! Aqui é da Softio.');
    // O caso que o dono não imagina.
    expect(c.previaSemNome()).toBe('Oi, ! Aqui é da Softio.');
    expect(c.previasDiferentes()).withContext('a tela mostra os dois').toBeTrue();
  });

  it('COM {{saudacao}} OS DOIS CASOS FICAM CERTOS', () => {
    montar();

    c.fNpsTexto.set('{{saudacao}} Aqui é da {{empresa}}.');

    expect(c.previaComNome()).toBe('Oi, Maria! Aqui é da Softio.');
    // Sem nome, a pontuação acompanha — é o que `NomeDePessoa.Saudacao` resolve no servidor.
    expect(c.previaSemNome()).toBe('Oi! Aqui é da Softio.');
  });

  /** ⚠️ VARIÁVEL INVENTADA SAI LITERAL no WhatsApp do cliente, com as chaves e tudo. O aviso é a
   *  única chance de o dono perceber antes de salvar. */
  it('VARIÁVEL QUE NÃO EXISTE É DENUNCIADA', () => {
    montar();

    c.fNpsTexto.set('Oi {{telefone}}, de 0 a 10?');
    expect(c.temVariavelDesconhecida()).toBeTrue();

    c.fNpsTexto.set('{{saudacao}} De 0 a 10?');
    expect(c.temVariavelDesconhecida()).toBeFalse();
  });

  /** A frase do sucesso diz QUANDO vale. Sem isso o dono muda o texto e acha que corrigiu o que já
   *  saiu — e pesquisa agendada mantém o texto que recebeu. */
  it('SALVAR DESLIGA O ESTADO DE SALVANDO E RECARREGA A TELA', () => {
    montar();

    c.salvarPesquisaNps();
    expect(c.salvandoNps()).toBeTrue();

    http.expectOne(r => r.url.endsWith('/configuracao/pesquisa-nps')).flush(null);
    drenarRecarga();

    expect(c.salvandoNps()).toBeFalse();
  });

  /** ===================== A PRÉVIA TEM DE ESTAR NA TELA, NÃO SÓ NO `computed` =====================
   *
   *  ⚠️ ESTE TESTE NASCEU DE UMA SABOTAGEM QUE NÃO DERRUBAVA NADA. Troquei o `@if (previasDiferentes())`
   *  do template por `@if (false)` — a prévia do caso ruim desaparecia da tela — e os cinco testes
   *  anteriores continuavam verdes, porque todos leem o `computed` e nenhum lê o DOM.
   *
   *  E a prévia do caso ruim é o ponto todo da seção: ela é a única chance de o dono ver "Oi, !"
   *  antes de o cliente ver.
   *  ============================================================================================== */
  it('OS DOIS BALÕES DE PRÉVIA APARECEM NA TELA', () => {
    montar();

    c.fNpsTexto.set('Oi, {{nome}}! Aqui é da {{empresa}}.');
    fixture.detectChanges();

    const baloes = [...(fixture.nativeElement as HTMLElement)
      .querySelectorAll('.previa-nps .previa-balao p')].map(e => e.textContent!.trim());

    expect(baloes.length).withContext('o bom e o ruim, lado a lado').toBe(2);
    expect(baloes[0]).toBe('Oi, Maria! Aqui é da Softio.');
    expect(baloes[1]).toBe('Oi, ! Aqui é da Softio.');
  });

  /** Com `{{saudacao}}` as duas prévias dão certo, e aí o segundo balão SAI da tela: repetir a
   *  mesma frase duas vezes não ensina nada e vira ruído. */
  it('COM AS DUAS PRÉVIAS IGUAIS, O SEGUNDO BALÃO NÃO APARECE', () => {
    montar();

    c.fNpsTexto.set('De 0 a 10, quanto você recomendaria?');
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement)
      .querySelectorAll('.previa-nps .previa-balao').length).toBe(1);
  });

  /** O aviso de variável desconhecida também mora no template. */
  it('O AVISO DE VARIÁVEL DESCONHECIDA APARECE NA TELA', () => {
    montar();

    const aviso = () => (fixture.nativeElement as HTMLElement).querySelector('.aviso-variavel');

    c.fNpsTexto.set('{{saudacao}} De 0 a 10?');
    fixture.detectChanges();
    expect(aviso()).toBeNull();

    c.fNpsTexto.set('Oi {{telefone}}, de 0 a 10?');
    fixture.detectChanges();
    expect(aviso()).not.toBeNull();
  });
});
